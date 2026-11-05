using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor
{
// Самотест --selftest (перенесён из десктопа без изменений логики) (порт BpMonitor.cs)
partial class Profile
{
	// ----------------------------- самотестирование: BpMonitor.exe --selftest -----------------------------
	// Запускается Program.Main ДО Load(): пути переназначены во временный каталог, реальные
	// config.json/data.bin не затрагиваются. Покрывает: FireToday, JT, окна join/нейтрали FeatsFor,
	// PK-фичи, round-trip бинарного журнала (append/правка/tombstone/оборванный хвост/компакция).
	// Возвращает число провалов (0 = OK).
	public int SelfTest(){
		int failed=0;
		Action<string,Action> check=(name,f)=>{
			try{ f(); Console.WriteLine("PASS  "+name); }
			catch(Exception ex){ failed++; Console.WriteLine("FAIL  "+name+": "+ex.Message); }
		};
		CultureInfo savedCulture=Thread.CurrentThread.CurrentCulture;
		check("FireToday: daily",()=>{
			if(!FireToday(new AlarmDef{Mode="daily"},DateTime.Now)) throw new Exception("daily должен срабатывать");
		});
		check("FireToday: days (ISO 1=Пн..7=Вс)",()=>{
			DateTime now=DateTime.Now;
			int iso=(int)now.DayOfWeek; if(iso==0) iso=7;
			if(!FireToday(new AlarmDef{Mode="days",Days=new List<int>{iso}},now)) throw new Exception("сегодняшний день должен срабатывать");
			if(FireToday(new AlarmDef{Mode="days",Days=new List<int>{iso==1?2:1}},now)) throw new Exception("чужой день не должен срабатывать");
			if(FireToday(new AlarmDef{Mode="days",Days=new List<int>()},now)) throw new Exception("пустой список дней не должен срабатывать");
		});
		check("FireToday: everyN + Anchor",()=>{
			DateTime now=DateTime.Now;
			string today=now.ToString("yyyyMMdd");
			if(!FireToday(new AlarmDef{Mode="everyN",EveryN=1},now)) throw new Exception("N=1 всегда срабатывает");
			if(!FireToday(new AlarmDef{Mode="everyN",EveryN=3,Anchor=""},now)) throw new Exception("пустой якорь = первый раз, срабатываем");
			if(!FireToday(new AlarmDef{Mode="everyN",EveryN=3,Anchor="мусор"},now)) throw new Exception("битый якорь = первый раз, срабатываем");
			if(!FireToday(new AlarmDef{Mode="everyN",EveryN=3,Anchor=today},now)) throw new Exception("якорь=сегодня -> 0%3=0, срабатывает");
			if(FireToday(new AlarmDef{Mode="everyN",EveryN=2,Anchor=now.AddDays(-1).ToString("yyyyMMdd")},now)) throw new Exception("1 день от якоря при N=2 не должен");
			if(!FireToday(new AlarmDef{Mode="everyN",EveryN=2,Anchor=now.AddDays(-2).ToString("yyyyMMdd")},now)) throw new Exception("2 дня от якоря при N=2 должен");
		});
		check("JT: форматы дат под ru-культурой",()=>{
			Thread.CurrentThread.CurrentCulture=new CultureInfo("ru-RU");
			DateTime d1=JT(JObject.Parse("{\"t\":\"2026-08-22T14:30:45\"}"),"t");// с секундами -> Date JValue (исторический баг)
			if(d1.Year!=2026||d1.Month!=8||d1.Day!=22||d1.Hour!=14||d1.Minute!=30) throw new Exception("с секундами: "+d1.ToString("o"));
			DateTime d2=JT(JObject.Parse("{\"t\":\"2026-08-22T14:30\"}"),"t");// без секунд -> строка
			if(d2.Month!=8||d2.Day!=22||d2.Hour!=14) throw new Exception("без секунд: "+d2.ToString("o"));
			DateTime d3=JT(JObject.Parse("{\"t\":\"2026-08-22\"}"),"t");
			if(d3.Date!=new DateTime(2026,8,22)) throw new Exception("только дата: "+d3.ToString("o"));
			try{ JT(JObject.Parse("{}"),"t"); throw new Exception("нет поля — должен бросать"); }
			catch(FormatException){}
		});
		check("FeatsFor: окна join и нейтрали",()=>{
			Cfg=new Config(); Cfg.Meds=new List<string>();
			D=new DataStore();
			DateTime t0=new DateTime(2026,8,22,12,0,0);// суббота
			Measurement m=new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0,Sys=120,Dia=80,Pulse=70};
			double[] x=FeatsFor(m);
			if(x.Length!=15) throw new Exception("InDim без лекарств = 15, получили "+x.Length);
			if(Math.Abs(x[8])>1e-9||Math.Abs(x[9])>1e-9||Math.Abs(x[10]-0.75)>1e-6) throw new Exception("нейтрали: эмоция 0, шум 0, сон 7.5ч");
			if(Math.Abs(x[11])>1e-9) throw new Exception("нейтраль качества сна = 5 -> 0, x[11]="+x[11]);
			if(Math.Abs(x[13])>1e-9||Math.Abs(x[14])>1e-9) throw new Exception("без dob/пола возраст и пол должны быть нейтральны 0");
			Cfg.Dob="1980-06-15"; Cfg.Sex="m";
			x=FeatsFor(m);
			double ageExp=Clamp(((t0-new DateTime(1980,6,15)).TotalDays/365.25-50.0)/40.0,-1.0,1.0);
			if(Math.Abs(x[13]-ageExp)>1e-9) throw new Exception("возраст на момент измерения: x[13]="+x[13]+" ждём "+ageExp);
			if(Math.Abs(x[14]-0.5)>1e-9) throw new Exception("пол m -> 0.5, x[14]="+x[14]);
			Cfg.Dob=""; Cfg.Sex="";
			if(Math.Abs(x[0]-Math.Sin(2*Math.PI*12/24))>1e-9) throw new Exception("час sin (12:00)");
			double dow=((int)t0.DayOfWeek+6)%7;
			if(Math.Abs(x[2]-Math.Sin(2*Math.PI*dow/7))>1e-9) throw new Exception("день sin");
			D.E.Add(new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddMinutes(5),Val=6});// +5 мин -> в окне
			D.E.Add(new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddMinutes(15),Val=-9});// +15 мин -> мимо
			D.N.Add(new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(-2),Type="собака"});
			D.N.Add(new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(-2),Type="собака"});// тот же тип
			D.N.Add(new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(-4),Type="стройка"});// вне окна 3ч
			D.S.Add(new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(-20),Hours=8,Quality=5});
			x=FeatsFor(m);
			if(Math.Abs(x[8]-0.6)>1e-6) throw new Exception("эмоция +5 мин должна учитываться, x[8]="+x[8]);
			if(Math.Abs(x[9]-0.25)>1e-6) throw new Exception("шум: 1 тип/4, x[9]="+x[9]);
			if(Math.Abs(x[10]-0.8)>1e-6) throw new Exception("сон 8ч -> 0.8, x[10]="+x[10]);
			if(Math.Abs(x[11])>1e-6) throw new Exception("качество 5 -> нейтраль 0, x[11]="+x[11]);
			D.S.Clear();
			D.S.Add(new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(-20),Hours=8,Quality=8});
			x=FeatsFor(m);
			if(Math.Abs(x[11]-0.6)>1e-6) throw new Exception("качество 8 -> (8-5)/5=0.6, x[11]="+x[11]);
			D.S.Clear();
			D.E.Clear();
			D.E.Add(new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(-13),Val=-8});// вне окна 12ч
			x=FeatsFor(m);
			if(Math.Abs(x[8])>1e-9) throw new Exception("эмоция вне окна -> нейтраль");
		});
		check("FeatsFor: PK-фичи лекарства",()=>{
			Cfg=new Config(); Cfg.Meds=new List<string>{"тестмед"};// неизвестен -> 8/12 ч
			D=new DataStore();
			DateTime t0=new DateTime(2026,8,22,12,0,0);
			Measurement m=new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0,Sys=120,Dia=80,Pulse=70,Meds=new List<string>{"тестмед"}};
			D.M.Add(m);
			double[] x=FeatsFor(m);
			if(x.Length!=17) throw new Exception("InDim с 1 лекарством = 17");
			if(Math.Abs(x[15]-1.0/3)>1e-6) throw new Exception("приём в момент t: conc=1 -> 1/3, x[15]="+x[15]);
			if(Math.Abs(x[16]-1.0)>1e-6) throw new Exception("в окне действия -> 1, x[16]="+x[16]);
			double[] x2=FeatsFor(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(8),Sys=120,Dia=80});
			if(Math.Abs(x2[15]-0.5/3)>1e-6) throw new Exception("через T½=8ч: conc=0.5 -> 1/6, x[15]="+x2[15]);
			if(Math.Abs(x2[16]-(1.0-8.0/12))>1e-6) throw new Exception("окно dur=12ч: win=1-8/12, x[16]="+x2[16]);
			double[] x3=FeatsFor(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(13),Sys=120,Dia=80});
			if(Math.Abs(x3[16])>1e-9) throw new Exception("за окном действия -> 0");
		});
		check("Validate: holdout-раздел + метрики + значимость",()=>{
			Cfg=new Config(); Cfg.Meds=new List<string>();
			D=new DataStore();
			Random r=new Random(7);
			DateTime t0=new DateTime(2026,9,1,8,0,0);
			for(int i=0;i<60;i++){// sys линейно зависит от атмосферного давления — единственная информативная фича
				float press=740+r.Next(0,40);
				D.M.Add(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(i*8.0),Sys=(int)(press-620),Dia=80,Pulse=70,PressMm=press});
			}
			JObject v=Validate(12);
			if((int)v["nTrain"]!=48||(int)v["nHold"]!=12) throw new Exception("раздел 48/12, получили "+(int)v["nTrain"]+"/"+(int)v["nHold"]);
			JArray imp=(JArray)v["importance"];
			if(imp.Count!=InDim()) throw new Exception("значимостей "+imp.Count+", InDim="+InDim());
			double mae=(double)v["maeSys"],rmse=(double)v["rmseSys"];
			if(mae<0||rmse<mae) throw new Exception("MAE="+mae+" RMSE="+rmse+" (RMSE должен быть >= MAE)");
			if((double)v["maeSys"]>25) throw new Exception("модель не выучила линейную связь с давлением: MAE="+mae);
			// перестановка информативной колонки должна увеличивать MSE сильнее прочего
			JObject pressImp=imp.Cast<JObject>().First(o=>(string)o["name"]=="давл. мм");
			double maxOther=imp.Cast<JObject>().Where(o=>(string)o["name"]!="давл. мм").Max(o=>(double)o["d"]);
			if((double)pressImp["d"]<=maxOther) throw new Exception("«давл. мм» не оказалась главной фичей: d="+(double)pressImp["d"]+", maxOther="+maxOther);
			// авто-раздел: n=60 -> hold=12
			JObject va=Validate(0);
			if((int)va["nHold"]!=12) throw new Exception("авто-holdout n/5=12, получили "+(int)va["nHold"]);
			// мало данных — ошибка, а не тихий ноль
			D.M.RemoveRange(0,45);
			try{ Validate(0); throw new Exception("при <20 записей должен бросать"); }
			catch(Exception ex){ if(ex.Message=="при <20 записей должен бросать") throw; }
		});
		check("data.bin: append/правка/tombstone round-trip",()=>{
			D=new DataStore();
			DateTime t0=new DateTime(2026,8,22,12,0,0);
			string id1=Guid.NewGuid().ToString("N"),id2=Guid.NewGuid().ToString("N"),emoId=Guid.NewGuid().ToString("N");
			AppendMeasurement(new Measurement{Id=id1,T=t0,Sys=120,Dia=80,Pulse=70,Note="заметка",Meds=new List<string>{"эналаприл"},PressMm=760f,TempC=15.5f,Hum=60f,Kp=33,Wx="ясно"});
			AppendMeasurement(new Measurement{Id=id2,T=t0.AddHours(1),Sys=140,Dia=90,Pulse=80});
			AppendMeasurement(new Measurement{Id=id1,T=t0,Sys=125,Dia=85,Pulse=72,Note="исправлено",Meds=new List<string>{"эналаприл"}});// правка = полная копия
			AppendGuidRec(RecMeasDel,id2);
			AppendEmotion(new EmotionEntry{Id=emoId,T=t0,Val=4,Note="ок"});
			AppendGuidRec(RecEmoDel,emoId);
			AppendNoise(new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=t0,Type="стройка",Note=""});
			AppendSleep(new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=t0,Hours=7,Quality=6,Note=""});
			D.LastPopup=new DateTime(2026,8,21,9,0,0); AppendPopup();
			D=new DataStore(); LoadBin();
			if(D.M.Count!=1) throw new Exception("после tombstone измерений: "+D.M.Count+" (ожидалось 1)");
			Measurement m=D.M[0];
			if(m.Id!=id1||m.Sys!=125||m.Dia!=85||m.Note!="исправлено") throw new Exception("правка не выиграла");
			if(m.PressMm!=null||m.Kp!=null) throw new Exception("правка без погоды должна обнулять снимок");
			if(m.Meds.Count!=1||m.Meds[0]!="эналаприл") throw new Exception("пул строк: лекарства");
			if(D.E.Count!=0) throw new Exception("tombstone эмоции не сработал");
			if(D.N.Count!=1||D.S.Count!=1) throw new Exception("журнал: N="+D.N.Count+" S="+D.S.Count);
			if(D.LastPopup==null||D.LastPopup.Value!=new DateTime(2026,8,21,9,0,0)) throw new Exception("LastPopup");
		});
		check("data.bin: оборванный хвост после краша",()=>{
			using(FileStream fs=File.Open(DataBinPath,FileMode.Append)) fs.Write(new byte[]{0x10,0x40,0x00,0x00,0x00,1,2,3},0,8);// запись обещает 0x40 байт, файл обрывается
			D=new DataStore(); LoadBin();// не должен бросить: что прочитано, то прочитано
			if(D.M.Count!=1) throw new Exception("записи до хвоста потеряны: "+D.M.Count);
		});
		check("Save(): компакция data.bin",()=>{
			long before=new FileInfo(DataBinPath).Length;
			for(int i=0;i<50;i++) AppendMeasurement(D.M[0]);// 50 мусорных копий
			D=new DataStore(); LoadBin();
			Save();
			long after=new FileInfo(DataBinPath).Length;
			if(after>=before) throw new Exception("после компакции файл должен стать меньше: "+before+" -> "+after);
			D=new DataStore(); LoadBin();
			if(D.M.Count!=1||D.N.Count!=1||D.S.Count!=1) throw new Exception("данные потеряны при компакции");
		});
		check("medpk.json: создание из эталона, чтение, MedKtFor",()=>{
			string p=Path.Combine(ExeDir,"medpk.json");
			if(File.Exists(p)) File.Delete(p);
			LoadMedPk();
			if(!File.Exists(p)) throw new Exception("medpk.json не создан");
			if(!MedPkDb.ContainsKey("эналаприл")) throw new Exception("эталон не записан");
			MedPkDb["тестмед"]=new[]{6.0,18.0}; SaveMedPk();
			MedPkDb.Clear(); LoadMedPk();
			if(!MedPkDb.ContainsKey("тестмед")) throw new Exception("пользовательская запись потеряна при перезагрузке");
			(double half,double dur)=MedKtFor("Тест-Мед!");// нормализация названия
			if(Math.Abs(half-6)>1e-9||Math.Abs(dur-18)>1e-9) throw new Exception("MedKtFor не взял значения из справочника");
			MedPkDb.Remove("тестмед"); SaveMedPk();
		});
		check("MedTake: round-trip и union с измерениями",()=>{
			Cfg=new Config(); Cfg.Meds=new List<string>{"тестмед"}; D=new DataStore();
			DateTime t0=new DateTime(2026,8,22,12,0,0);
			string del=Guid.NewGuid().ToString("N");
			AppendMedTake(new MedTakeEntry{Id=del,T=t0.AddHours(-5),Med="удалённое"});
			AppendGuidRec(RecMedTakeDel,del);
			string keep=Guid.NewGuid().ToString("N");
			AppendMedTake(new MedTakeEntry{Id=keep,T=t0.AddHours(8),Med="тестмед"});
			D=new DataStore(); LoadBin();
			if(D.MedTakes.Count!=1||D.MedTakes[0].Med!="тестмед"||D.MedTakes[0].T!=t0.AddHours(8)) throw new Exception("round-trip: "+D.MedTakes.Count);
			// union: приём с измерения (t0) + приём из журнала (t0+8ч)
			D.M.Add(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0,Sys=120,Dia=80,Meds=new List<string>{"тестмед"}});
			Dictionary<string,List<DateTime>> ints=MedIntakes();
			List<DateTime> ts;
			if(!ints.TryGetValue("тестмед",out ts)||ts.Count!=2) throw new Exception("union: "+(ts==null?0:ts.Count));
			// at t0+8h: conc = 0.5^(8/8) + 0.5^0 = 1.5 -> x=min(1.5,3)/3 = 0.5 (тестмед удалён из medpk -> дефолт 8/12)
			double[] x=FeatsFor(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(8),Sys=120,Dia=80});
			if(Math.Abs(x[15]-0.5)>1e-6) throw new Exception("PK-фича по двум приёмам: x[15]="+x[15]);
			if(Math.Abs(x[16]-1.0)>1e-6) throw new Exception("окно действия свежего приёма -> 1, x[16]="+x[16]);
		});
		check("DailyBackup: архив создаётся один раз в сутки",()=>{
			string bdir=Path.Combine(ExeDir,"backups");
			if(Directory.Exists(bdir)) Directory.Delete(bdir,true);
			DailyBackup();
			string[] z1=Directory.GetFiles(Path.Combine(ExeDir,"backups"),"*.zip");
			if(z1.Length!=1) throw new Exception("архивов: "+z1.Length);
			using(ZipArchive z=ZipFile.OpenRead(z1[0])) if(z.Entries.Count==0) throw new Exception("пустой архив");
			DailyBackup();
			if(Directory.GetFiles(Path.Combine(ExeDir,"backups"),"*.zip").Length!=1) throw new Exception("второй вызов за сутки не должен плодить архивы");
		});
		check("Tune: сетка гиперпараметров и выбор",()=>{
			Cfg=new Config(); Cfg.Meds=new List<string>();
			D=new DataStore();
			Random r=new Random(11);
			DateTime t0=new DateTime(2026,9,1,8,0,0);
			for(int i=0;i<60;i++){
				float press=740+r.Next(0,40);
				D.M.Add(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(i*8.0),Sys=(int)(press-620),Dia=80,Pulse=70,PressMm=press});
			}
			JObject t=Tune(12);
			if((int)t["nTrain"]!=48||(int)t["nHold"]!=12) throw new Exception("раздел 48/12");
			JArray res=(JArray)t["results"];
			if(res.Count!=9) throw new Exception("конфигураций сетки: "+res.Count+" (ожидалось 9)");
			int bestH=(int)t["hidden"],bestH2=(int)t["hidden2"];
			if(new[]{16,28,40}.Contains(bestH)==false||new[]{10,16,24}.Contains(bestH2)==false) throw new Exception("лучшая конфигурация вне сетки: "+bestH+"/"+bestH2);
			if(Cfg.Nn.Hidden!=bestH||Cfg.Nn.Hidden2!=bestH2) throw new Exception("лучшая конфигурация не применена в Cfg.Nn");
			if(D.Nn.W1.GetLength(0)!=bestH||D.Nn.W1b.GetLength(0)!=bestH2) throw new Exception("рабочая модель не пересобрана под выбранные размеры");
			// отсортировано по возрастанию holdMse, лучший — первый
			if((double)res[0]["holdMse"]>(double)res[res.Count-1]["holdMse"]) throw new Exception("результаты не отсортированы по holdMse");
		});
		check("Экспорт/импорт всего: round-trip",()=>{
			Cfg=new Config(); Cfg.Meds=new List<string>{"эналаприл"}; Cfg.NormSys=140;
			D=new DataStore();
			D.Nn=NewNn(Cfg.Nn.Hidden,Cfg.Nn.Hidden2,InDim());
			DateTime t0=new DateTime(2026,8,22,12,0,0);
			D.M.Add(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0,Sys=130,Dia=85,Pulse=72,Note="первое",Meds=new List<string>{"эналаприл"},PressMm=755f,Kp=33});
			D.M.Add(new Measurement{Id=Guid.NewGuid().ToString("N"),T=t0.AddHours(3),Sys=125,Dia=80,Pulse=70});
			D.E.Add(new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=t0,Val=4,Note="ок"});
			D.N.Add(new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=t0,Type="стройка"});
			D.S.Add(new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=t0,Hours=7,Quality=6});
			D.MedTakes.Add(new MedTakeEntry{Id=Guid.NewGuid().ToString("N"),T=t0,Med="эналаприл"});
			D.LastPopup=new DateTime(2026,8,21,9,0,0);
			JObject dump=ExportAll();
			string text=dump.ToString(Newtonsoft.Json.Formatting.None);
			// «перенос на другой ПК»: чистое состояние -> импорт
			Cfg=new Config(); D=new DataStore(); D.Nn=NewNn(4,4,InDim());
			string summary=ImportAll(JObject.Parse(text));
			if(D.M.Count!=2||D.E.Count!=1||D.N.Count!=1||D.S.Count!=1||D.MedTakes.Count!=1) throw new Exception("после импорта: "+summary);
			if(Cfg.NormSys!=140||!Cfg.Meds.Contains("эналаприл")) throw new Exception("конфиг не восстановлен");
			Measurement m=D.M.OrderBy(x=>x.T).First();
			if(m.Sys!=130||m.Note!="первое"||m.Kp!=33||m.Meds.Count!=1) throw new Exception("измерение потеряно/испорчено");
			if(D.LastPopup==null||D.LastPopup.Value!=new DateTime(2026,8,21,9,0,0)) throw new Exception("LastPopup");
			if(D.Nn.W1.GetLength(1)!=InDim()) throw new Exception("размерность модели после импорта: "+D.Nn.W1.GetLength(1)+" != "+InDim());
			if(!File.Exists(DataBinPath)||!IsBpmn(DataBinPath)) throw new Exception("data.bin после импорта не переписан");
			// чужой файл отвергается
			try{ ImportAll(JObject.Parse("{\"app\":\"other\",\"ver\":1}")); throw new Exception("чужой файл должен отвергаться"); }
			catch(Exception ex){ if(ex.Message=="чужой файл должен отвергаться") throw; }
		});
		check("Фаза 2: сессии, rate-limit, cap пользователей",()=>{
			string root=Path.Combine(ExeDir,"auth-test");
			if(Directory.Exists(root)) Directory.Delete(root,true);
			Directory.CreateDirectory(Path.Combine(root,"users"));
			UserStore.Root=root; UserStore.Cfg=new AuthCfg{MaxUsers=2};
			Sessions.Init(root);
			if(UserStore.GetOrCreate("a@xor.world")==null) throw new Exception("первый пользователь не создан");
			if(UserStore.GetOrCreate("b@xor.world")==null) throw new Exception("второй не создан");
			if(UserStore.GetOrCreate("c@xor.world")!=null) throw new Exception("cap не сработал: третий создан при MaxUsers=2");
			if(UserStore.GetOrCreate("a@xor.world")==null) throw new Exception("существующий при полном cap должен возвращаться");
			string tok=Sessions.Create("a@xor.world",1);
			var ss=Sessions.Resolve(tok);
			if(ss==null||ss.Uid!="a@xor.world") throw new Exception("сессия не резолвится");
			if(Sessions.Resolve("net-takogo-tokena")!=null) throw new Exception("неизвестный токен резолвится");
			if(!File.Exists(Path.Combine(root,"sessions.json"))) throw new Exception("sessions.json не записан");
			Sessions.Destroy(tok);
			if(Sessions.Resolve(tok)!=null) throw new Exception("сессия не уничтожена");
			if(!RateLimit.Allow("k",1)) throw new Exception("чистый ключ заблокирован");
			RateLimit.Fail("k",1);
			if(RateLimit.Allow("k",1)) throw new Exception("после Fail не заблокирован");
			Thread.Sleep(1100);// backoff FailDelaySec=1
			if(!RateLimit.Allow("k",1)) throw new Exception("backoff не истёк");
			RateLimit.Ok("k");
			UserStore.Root=null;// самодостаточность: следующий запуск selftest в другой песочнице
		});
		Thread.CurrentThread.CurrentCulture=savedCulture;
		Console.WriteLine(failed==0?"SELFTEST OK":"SELFTEST FAILED: "+failed);
		return failed;
	}
}
}
