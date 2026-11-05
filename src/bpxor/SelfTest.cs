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
			catch(Exception ex){ failed++; Console.WriteLine("FAIL  "+name+": "+ex.Message); Console.WriteLine(ex.StackTrace); }
		};
		CultureInfo savedCulture=Thread.CurrentThread.CurrentCulture;
		check("FireToday: daily",()=>{
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="daily"},DateTime.Now)) throw new Exception("daily должен срабатывать");
		});
		check("FireToday: days (ISO 1=Пн..7=Вс)",()=>{
			DateTime now=DateTime.Now;
			int iso=(int)now.DayOfWeek; if(iso==0) iso=7;
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="days",Days=new List<int>{iso}},now)) throw new Exception("сегодняшний день должен срабатывать");
			if(AlarmScheduler.FireToday(new AlarmDef{Mode="days",Days=new List<int>{iso==1?2:1}},now)) throw new Exception("чужой день не должен срабатывать");
			if(AlarmScheduler.FireToday(new AlarmDef{Mode="days",Days=new List<int>()},now)) throw new Exception("пустой список дней не должен срабатывать");
		});
		check("FireToday: everyN + Anchor",()=>{
			DateTime now=DateTime.Now;
			string today=now.ToString("yyyyMMdd");
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="everyN",EveryN=1},now)) throw new Exception("N=1 всегда срабатывает");
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="everyN",EveryN=3,Anchor=""},now)) throw new Exception("пустой якорь = первый раз, срабатываем");
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="everyN",EveryN=3,Anchor="мусор"},now)) throw new Exception("битый якорь = первый раз, срабатываем");
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="everyN",EveryN=3,Anchor=today},now)) throw new Exception("якорь=сегодня -> 0%3=0, срабатывает");
			if(AlarmScheduler.FireToday(new AlarmDef{Mode="everyN",EveryN=2,Anchor=now.AddDays(-1).ToString("yyyyMMdd")},now)) throw new Exception("1 день от якоря при N=2 не должен");
			if(!AlarmScheduler.FireToday(new AlarmDef{Mode="everyN",EveryN=2,Anchor=now.AddDays(-2).ToString("yyyyMMdd")},now)) throw new Exception("2 дня от якоря при N=2 должен");
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
		check("delta: курсор seq и записи",()=>{
			long gen=BinGen; long s0=BinSeq;
			AppendMeasurement(new Measurement{Id=Guid.NewGuid().ToString("N"),T=DateTime.Now.AddHours(-3),Sys=118,Dia=76,Pulse=64});
			AppendEmotion(new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=DateTime.Now.AddHours(-2),Val=3,Note=""});
			JObject d=DeltaJson(s0,gen,RevCfg,RevNn,RevWx,RevMedPk);
			if(d["reset"]!=null) throw new Exception("неожиданный reset");
			JArray meas=d["meas"] as JArray; JArray emo=d["emo"] as JArray;
			if(meas==null||meas.Count!=1) throw new Exception("meas: "+(meas==null?"null":meas.Count.ToString()));
			if(emo==null||emo.Count!=1) throw new Exception("emo: "+(emo==null?"null":emo.Count.ToString()));
			if((long)d["seq"]<=s0) throw new Exception("seq не вырос");
			if(!(bool)d["changed"]) throw new Exception("changed должен быть true");
			JObject d2=DeltaJson((long)d["seq"],gen,RevCfg,RevNn,RevWx,RevMedPk);
			if((bool)d2["changed"]) throw new Exception("на чистом курсоре changed должен быть false");
			if((long)d2["seq"]!=(long)d["seq"]) throw new Exception("seq уехал");
		});
		check("delta: reset при компакции и битом курсоре",()=>{
			long oldSeq=BinSeq,oldGen=BinGen;
			Save(); // компакция -> новое поколение
			if(BinGen==oldGen) throw new Exception("gen не вырос после Save()");
			JObject d=DeltaJson(oldSeq,oldGen,RevCfg,RevNn,RevWx,RevMedPk);
			if(d["reset"]==null) throw new Exception("старый gen должен давать reset");
			JObject d2=DeltaJson(BinSeq+1000,BinGen,RevCfg,RevNn,RevWx,RevMedPk);
			if(d2["reset"]==null) throw new Exception("since>len должен давать reset");
			JObject d3=DeltaJson(BinSeq,BinGen,RevCfg,RevNn,RevWx,RevMedPk);
			if(d3["reset"]!=null||(bool)d3["changed"]) throw new Exception("свежий курсор должен быть чист");
		});
		check("delta: ревизии cfg/medpk выносят блок",()=>{
			Cfg.NormSys=139; SaveCfg();
			JObject d=DeltaJson(BinSeq,BinGen,RevCfg-1,RevNn,RevWx,RevMedPk);
			if(d["cfg"]==null) throw new Exception("нет блока cfg");
			if((int)d["cfg"]["NormSys"]!=139) throw new Exception("содержимое cfg");
			if(d["medPk"]==null) throw new Exception("нет блока medPk");
			Cfg.NormSys=135; SaveCfg();
		});
		check("measurements: окно before/limit",()=>{
			for(int i=0;i<5;i++) D.M.Add(new Measurement{Id=Guid.NewGuid().ToString("N"),T=DateTime.Now.AddDays(-10-i),Sys=120,Dia=80,Pulse=70});
			JObject w=MeasurementsWindow(DateTime.Now,2);
			if(((JArray)w["items"]).Count!=2) throw new Exception("limit не сработал");
			if(!(bool)w["hasMore"]) throw new Exception("hasMore должен быть true");
			DateTime oldest=((JObject)((JArray)w["items"])[1])["T"].Value<DateTime>();
			JObject w2=MeasurementsWindow(oldest,100);
			if(((JArray)w2["items"]).Count==0) throw new Exception("старше not пришли");
			if(((JArray)w2["items"]).Any(x=>x["T"].Value<DateTime>()>=oldest)) throw new Exception("before не соблюдён");
		});
		check("state: sync-блок, кап окна, ETag",()=>{
			if(D.Nn==null) D.Nn=NewNn(Cfg.Nn.Hidden,Cfg.Nn.Hidden2,InDim());// в песочнице модель не загружалась
			JObject st=StateJson();
			if(st["sync"]==null) throw new Exception("нет sync");
			if((long)st["sync"]["seq"]!=BinSeq) throw new Exception("seq не совпал");
			if(st["measTotal"]==null||(int)st["measTotal"]!=D.M.Count) throw new Exception("measTotal");
			if(((JArray)st["measurements"]).Count>D.M.Count) throw new Exception("окно больше всей базы");
			string e1=SyncEtag;
			AppendMeasurement(new Measurement{Id=Guid.NewGuid().ToString("N"),T=DateTime.Now,Sys=122,Dia=81,Pulse=69});
			if(SyncEtag==e1) throw new Exception("ETag не прореагировал на append");
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
		check("Фаза 3: WxCache — общий кэш по округлённым координатам",()=>{
			WxCache.Clear();
			if(WxCache.Key(55.751,37.619)!=WxCache.Key(55.752,37.621)) throw new Exception("близкие координаты должны давать один ключ");
			if(WxCache.Key(55.75,37.619)==WxCache.Key(56.75,37.619)) throw new Exception("разные широты дали один ключ");
			string v;
			if(WxCache.Try("live:тест",10,out v)) throw new Exception("hit на пустом кэше");
			WxCache.Put("live:тест","данные");
			if(!WxCache.Try("live:тест",10,out v)||v!="данные") throw new Exception("miss после Put");
			if(!WxCache.Try("live:тест",-1,out v)) throw new Exception("бессрочный ttl должен попадать");
			WxCache.Clear();
		});
		check("Фаза 3: JobGate — глобальная очередь, max 1",()=>{
			JobGate.Reset(); JobGate.Max=1;
			var aStarted=new ManualResetEventSlim(false);
			var aRelease=new ManualResetEventSlim(false);
			var order=new List<string>(); object olk=new object();
			JobGate.Run(()=>{ lock(olk) order.Add("A"); aStarted.Set(); aRelease.Wait(); });
			if(!aStarted.Wait(2000)) throw new Exception("A не запустилась сразу");
			bool bq=false;
			JobGate.Run(()=>{ lock(olk) order.Add("B"); },()=>bq=true);
			Thread.Sleep(150);
			if(!bq||JobGate.Pending!=1) throw new Exception("B не встала в очередь (queued="+bq+", pending="+JobGate.Pending+")");
			aRelease.Set();
			for(int i=0;i<50&&JobGate.Pending>0;i++) Thread.Sleep(50);
			Thread.Sleep(200);
			lock(olk){ if(order.Count!=2||order[0]!="A"||order[1]!="B") throw new Exception("порядок выполнения: "+string.Join(",",order)); }
			if(JobGate.Running!=0) throw new Exception("слоты не освободились: "+JobGate.Running);
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
		check("Фаза 4: планировщик — daily, догон 30 мин, без дублей",()=>{
			Config cfg=new Config(); cfg.Tz="UTC";
			cfg.Alarms=new List<AlarmDef>{new AlarmDef{Time="08:00",Label="утро"}};
			AlarmScheduler.State st=new AlarmScheduler.State();
			DateTime before=new DateTime(2026,11,5,7,55,0,DateTimeKind.Utc);// до цели
			if(AlarmScheduler.ComputeDue(cfg,st,before).AlarmKeys.Count!=0) throw new Exception("до цели не должен срабатывать");
			DateTime inside=new DateTime(2026,11,5,8,10,0,DateTimeKind.Utc);// в окне догона
			AlarmScheduler.Due due=AlarmScheduler.ComputeDue(cfg,st,inside);
			if(due.AlarmKeys.Count!=1) throw new Exception("в окне догона должен сработать");
			if(st.RingingKey!=due.AlarmKeys[0]||st.RingingLabel!="утро") throw new Exception("ringing-состояние не записано");
			if(AlarmScheduler.ComputeDue(cfg,st,inside).AlarmKeys.Count!=0) throw new Exception("повторный тик: дубль срабатывания");
			DateTime late=new DateTime(2026,11,5,8,35,0,DateTimeKind.Utc);// окно 30 мин прошло
			if(AlarmScheduler.ComputeDue(cfg,st,late).AlarmKeys.Count!=0) throw new Exception("за окном догона не должен срабатывать");
			if(!due.AlarmKeys[0].StartsWith("20261105_0_08:00")) throw new Exception("формат ключа: "+due.AlarmKeys[0]);
		});
		check("Фаза 4: планировщик — таймзона юзера (сервер в UTC)",()=>{
			Config cfg=new Config(); cfg.Tz="Europe/Moscow";// UTC+3
			cfg.Alarms=new List<AlarmDef>{new AlarmDef{Time="10:00"}};
			AlarmScheduler.State st=new AlarmScheduler.State();
			DateTime earlyUtc=new DateTime(2026,11,5,6,50,0,DateTimeKind.Utc);// Мск 09:50 — рано
			if(AlarmScheduler.ComputeDue(cfg,st,earlyUtc).AlarmKeys.Count!=0) throw new Exception("по Мск ещё 09:50 — не должен");
			DateTime okUtc=new DateTime(2026,11,5,7,10,0,DateTimeKind.Utc);// Мск 10:10 — в окне
			AlarmScheduler.Due due=AlarmScheduler.ComputeDue(cfg,st,okUtc);
			if(due.AlarmKeys.Count!=1||!due.AlarmKeys[0].StartsWith("20261105_0_10:00")) throw new Exception("по Мск 10:10 должен сработать, ключи: "+string.Join(",",due.AlarmKeys));
			Config bad=new Config(); bad.Tz="Нет/Такой/Зоны"; bad.Alarms=new List<AlarmDef>{new AlarmDef{Time="08:00"}};
			if(AlarmScheduler.ComputeDue(bad,new AlarmScheduler.State(),new DateTime(2026,11,5,8,10,0,DateTimeKind.Utc)).AlarmKeys.Count!=1) throw new Exception("битая tz — фолбэк UTC, должен сработать");
		});
		check("Фаза 4: планировщик — days/everyN + напоминания о лекарствах",()=>{
			DateTime nowUtc=new DateTime(2026,11,5,10,5,0,DateTimeKind.Utc);// 2026-11-05 = четверг (ISO 4)
			Config cfg=new Config(); cfg.Tz="UTC";
			cfg.Alarms=new List<AlarmDef>{
				new AlarmDef{Time="10:00",Mode="days",Days=new List<int>{4}},
				new AlarmDef{Time="10:00",Mode="days",Days=new List<int>{5}},
				new AlarmDef{Time="10:00",Mode="everyN",EveryN=2,Anchor="20261101"},
				new AlarmDef{Time="10:00",Mode="everyN",EveryN=2,Anchor="20261102"}
			};
			cfg.MedTimes=new Dictionary<string,List<string>>{{"тестмед",new List<string>{"10:00","23:00"}}};
			cfg.Meds=new List<string>{"Тест-Мед!"};
			AlarmScheduler.State st=new AlarmScheduler.State();
			AlarmScheduler.Due due=AlarmScheduler.ComputeDue(cfg,st,nowUtc);
			if(due.AlarmKeys.Count!=2) throw new Exception("должны: days(чт) + everyN(4 дня от якоря N=2), получили "+due.AlarmKeys.Count);
			if(due.MedKeys.Count!=1) throw new Exception("мед-напоминание 10:00 должно сработать, 23:00 — нет");
			if(due.MedLabels.Count!=1||due.MedLabels[0]!="Тест-Мед!") throw new Exception("нормализация ключа MedTimes -> название: "+string.Join(",",due.MedLabels));
			if(AlarmScheduler.ComputeDue(cfg,st,nowUtc).MedKeys.Count!=0) throw new Exception("дубль мед-напоминания");
			// everyN без якоря: фиксируется якорь = сегодня
			Config cfg2=new Config(); cfg2.Tz="UTC";
			cfg2.Alarms=new List<AlarmDef>{new AlarmDef{Time="10:00",Mode="everyN",EveryN=3}};
			AlarmScheduler.State st2=new AlarmScheduler.State();
			AlarmScheduler.Due d2=AlarmScheduler.ComputeDue(cfg2,st2,nowUtc);
			if(d2.AlarmKeys.Count!=1||!d2.AnchorsChanged||cfg2.Alarms[0].Anchor!="20261105") throw new Exception("якорь everyN не зафиксирован");
		});
		check("Фаза 4: alarms.json — persist состояния",()=>{
			string dir=Path.Combine(ExeDir,"sched-test");
			if(Directory.Exists(dir)) Directory.Delete(dir,true);
			Directory.CreateDirectory(dir);
			string path=Path.Combine(dir,"alarms.json");
			AlarmScheduler.State st=AlarmScheduler.State.Load(path);
			st.Fired["k1"]=DateTime.UtcNow; st.RingingKey="k1"; st.RingingLabel="тест";
			st.Save();
			AlarmScheduler.State st2=AlarmScheduler.State.Load(path);
			if(!st2.Fired.ContainsKey("k1")||st2.RingingKey!="k1"||st2.RingingLabel!="тест") throw new Exception("round-trip потерян");
			st2.RingingKey=""; st2.DismissedKey="k1"; st2.Save();
			AlarmScheduler.State st3=AlarmScheduler.State.Load(path);
			if(st3.RingingKey!=""||st3.DismissedKey!="k1") throw new Exception("dismiss не персистится");
			Directory.Delete(dir,true);
		});
		check("Фаза 4: dismiss сбрасывается новым срабатыванием",()=>{
			Config cfg=new Config(); cfg.Tz="UTC";
			cfg.Alarms=new List<AlarmDef>{new AlarmDef{Time="08:00"},new AlarmDef{Time="09:00"}};
			AlarmScheduler.State st=new AlarmScheduler.State();
			AlarmScheduler.ComputeDue(cfg,st,new DateTime(2026,11,5,8,10,0,DateTimeKind.Utc));
			st.DismissedKey=st.RingingKey; st.RingingKey="";
			AlarmScheduler.Due due=AlarmScheduler.ComputeDue(cfg,st,new DateTime(2026,11,5,9,10,0,DateTimeKind.Utc));
			if(due.AlarmKeys.Count!=1) throw new Exception("09:00 должен сработать");
			if(st.DismissedKey!="") throw new Exception("dismiss не сброшен новым срабатыванием: "+st.DismissedKey);
		});
		check("Фаза 4: AlarmScheduler.LoadCfg — Replace, без дублей дефолтных будильников",()=>{
			Config cfg=new Config(); cfg.Tz="UTC"; cfg.Alarms=new List<AlarmDef>{new AlarmDef{Time="07:00",Label="один"}};
			string f=Path.Combine(ExeDir,"cfg-load-test.json");
			File.WriteAllText(f,JsonConvert.SerializeObject(cfg));
			Config back=AlarmScheduler.LoadCfg(f);
			if(back==null||back.Alarms.Count!=1||back.Alarms[0].Time!="07:00") throw new Exception("Replace-разбор вернул "+(back==null?"null":back.Alarms.Count.ToString())+" будильников");
			File.Delete(f);
		});
		check("Фаза 4: PushStore — добавление/дедуп/удаление/persist",()=>{
			string dir=Path.Combine(ExeDir,"push-test");
			if(Directory.Exists(dir)) Directory.Delete(dir,true);
			Directory.CreateDirectory(dir);
			PushStore.Init(dir);
			PushStore.Add("u@xor.world",new PushStore.Sub{Endpoint="https://push.example/a",P256dh="bk",Auth="YXV0"});
			PushStore.Add("u@xor.world",new PushStore.Sub{Endpoint="https://push.example/a",P256dh="bk2",Auth="YXV0"});// тот же endpoint -> замена
			if(PushStore.Count("u@xor.world")!=1) throw new Exception("дедуп по endpoint: "+PushStore.Count("u@xor.world"));
			PushStore.Add("u@xor.world",new PushStore.Sub{Endpoint="https://push.example/b",P256dh="bk",Auth="YXV0"});
			if(PushStore.Count("u@xor.world")!=2) throw new Exception("разные endpoint: "+PushStore.Count("u@xor.world"));
			PushStore.Init(dir);// перечитать с диска
			if(PushStore.Count("u@xor.world")!=2) throw new Exception("persist потерян");
			if(!PushStore.Remove("u@xor.world","https://push.example/a")) throw new Exception("Remove вернул false");
			if(PushStore.Remove("u@xor.world","https://net-takogo")) throw new Exception("Remove чужого вернул true");
			if(PushStore.Count("u@xor.world")!=1) throw new Exception("после удаления: "+PushStore.Count("u@xor.world"));
			Directory.Delete(dir,true);
		});
		check("Фаза 4: VAPID — генерация ключей и подпись JWT (WebPush)",()=>{
			string dir=Path.Combine(ExeDir,"vapid-test");
			if(Directory.Exists(dir)) Directory.Delete(dir,true);
			Directory.CreateDirectory(dir);
			VapidKeys.Init(dir);
			string pub=VapidKeys.PublicKey,priv=VapidKeys.PrivateKey;
			if(string.IsNullOrEmpty(pub)||string.IsNullOrEmpty(priv)) throw new Exception("ключи не сгенерированы");
			if(!File.Exists(Path.Combine(dir,"push","vapid.json"))) throw new Exception("vapid.json не записан");
			byte[] q=Convert.FromBase64String(pub.Replace('-','+').Replace('_','/').PadRight((pub.Length+3)/4*4,'='));
			if(q.Length!=65||q[0]!=4) throw new Exception("публичный ключ не uncompressed P-256 point");
			// проверяем, что библиотека принимает наш формат ключей: VapidHelper подписывает JWT (ES256)
			var vh=WebPush.VapidHelper.GetVapidHeaders("https://example.com/",VapidKeys.Subject,pub,priv,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60);// 5-й аргумент — unix-время истечения
			string jwt; vh.TryGetValue("Authorization",out jwt);
			if(string.IsNullOrEmpty(jwt)||!jwt.StartsWith("WebPush ")) throw new Exception("VAPID Authorization не сформирован: "+jwt);
			VapidKeys.Init(dir);// повторный Init читает с диска — те же ключи
			if(VapidKeys.PublicKey!=pub) throw new Exception("ключи не персистятся");
			Directory.Delete(dir,true);
		});
		check("Погода: парсер met.no (ApplyMetNo/MetNoWmo)",()=>{
			string fx="{\"properties\":{\"timeseries\":["
				+"{\"time\":\"2026-11-05T13:00:00Z\",\"data\":{\"instant\":{\"details\":{\"air_pressure_at_sea_level\":1000.2,\"air_temperature\":0.5,\"relative_humidity\":88}},\"now\":{\"summary\":{\"symbol_code\":\"partlycloudy_day\"}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\"lightrain\"},\"details\":{\"air_pressure_at_sea_level\":1000.0,\"air_temperature\":0.4,\"relative_humidity\":90}}}},"
				+"{\"time\":\"2026-11-05T14:00:00Z\",\"data\":{\"instant\":{\"details\":{\"air_pressure_at_sea_level\":999.8,\"air_temperature\":0.3,\"relative_humidity\":91}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\"snow\"},\"details\":{\"air_pressure_at_sea_level\":999.5,\"air_temperature\":0.1,\"relative_humidity\":92}}}},"
				+"{\"time\":\"2026-11-05T15:00:00Z\",\"data\":{\"instant\":{\"details\":{\"air_pressure_at_sea_level\":999.0,\"air_temperature\":0.0,\"relative_humidity\":93}},\"next_6_hours\":{\"summary\":{\"symbol_code\":\"cloudy\"}}}}"
				+"]}}";
			string savedTz=Cfg.Tz; Cfg.Tz="UTC"; Forecast=new List<WxHour>();
			try{
				ApplyMetNo(JObject.Parse(fx));
				if(Math.Abs(Weather.PressMm.Value-1000.2*0.750062f)>0.01f) throw new Exception("PressMm="+(object)Weather.PressMm);
				if(string.IsNullOrEmpty(Weather.Text)) throw new Exception("Text пустой");
				if(Forecast.Count!=2) throw new Exception("Forecast.Count="+Forecast.Count+" (next_6_hours не входит)");
				if(Forecast[0].Code!=80||Forecast[1].Code!=73) throw new Exception("коды: "+Forecast[0].Code+","+Forecast[1].Code);
				if(Forecast[0].T.Hour!=13) throw new Exception("час локальный: "+Forecast[0].T);
				if(Math.Abs((double)Forecast[0].Press-1000.0)>0.001) throw new Exception("Press hourly");
				if(WmoText(4)=="неизвестно") throw new Exception("WmoText(4) не покрыт");
			if(MetNoWmo("heavyrainshowers_night")!=82||MetNoWmo("clearsky_day")!=0||MetNoWmo("нет_такого")!=null) throw new Exception("MetNoWmo маппинг");
			} finally{ Cfg.Tz=savedTz; }
		});
		check("F1/F2: PWA-статика на месте (q.html, манифест, иконки)",()=>{
			string bin=AppContext.BaseDirectory;// ExeDir тут — временная песочница selftest, а не каталог бинаря
			foreach(string f in new string[]{"q.html","manifest.webmanifest","icons/icon-192.png","icons/icon-512.png","icons/icon-maskable-512.png","icons/apple-touch-icon.png"})
				if(!File.Exists(Path.Combine(bin,f))) throw new Exception("нет "+f+" рядом с бинарём (csproj CopyToOutputDirectory?)");
			string q=File.ReadAllText(Path.Combine(bin,"q.html"));
			if(q.IndexOf("q-sys")<0||q.IndexOf("/api/measurement")<0) throw new Exception("q.html неполный");
			JObject mf=JObject.Parse(File.ReadAllText(Path.Combine(bin,"manifest.webmanifest")));
			if(((JArray)mf["icons"]).Count<2) throw new Exception("в манифесте <2 иконок");
			if((string)mf["shortcuts"]?[0]?["url"]!="/q") throw new Exception("shortcut /q не в манифесте");
			if(!File.Exists(Path.Combine(bin,"alarm.mp3"))) throw new Exception("alarm.mp3 не рядом с бинарём");
		});
		check("Sync: API-токены (создание/проверка/отзыв/персист)",()=>{
			string dir=Path.Combine(ExeDir,"tok");
			Directory.CreateDirectory(dir);
			ApiTokens.Init(dir);
			var t=ApiTokens.Create("u1@test","ПК дома");
			if(string.IsNullOrEmpty(t.Token)||!t.Token.Contains(".")) throw new Exception("некорректный токен");
			if(ApiTokens.Resolve(t.Token)!="u1@test") throw new Exception("токен не резолвится");
			if(ApiTokens.Resolve(t.Token+"x")!=null) throw new Exception("подделанный токен не должен резолвиться");
			if(ApiTokens.Resolve("нет-такого")!=null) throw new Exception("мусор не должен резолвиться");
			if(ApiTokens.List("u1@test").Count!=1) throw new Exception("список не тот");
			ApiTokens.Init(dir);// перечитка с диска: хранится хэш, не токен
			if(ApiTokens.Resolve(t.Token)!="u1@test") throw new Exception("токен потерян после перечитки");
			if(!ApiTokens.Revoke("u1@test",t.Id)) throw new Exception("отзыв не сработал");
			if(ApiTokens.Resolve(t.Token)!=null) throw new Exception("отозванный токен резолвится");
			if(ApiTokens.Revoke("other@u",t.Id)) throw new Exception("чужой токен отозван");
		});
		check("Sync: push/pull дельта (LWW, echo, тумстоуны, cfg)",()=>{
			string dir=Path.Combine(ExeDir,"sync");
			Directory.CreateDirectory(dir);
			Profile s=new Profile(dir); s.Load();
			string id=Guid.NewGuid().ToString("N");
			string m1="{\"k\":\"meas\",\"Id\":\""+id+"\",\"T\":\"2026-11-01T09:00:00\",\"Sys\":120,\"Dia\":80,\"Pulse\":70,\"Note\":\"\"}";
			JObject r1=s.SyncPush(JObject.Parse("{\"recs\":["+m1+"]}"));
			if((int)r1["applied"]!=1) throw new Exception("первый push: applied="+(int)r1["applied"]);
			if(s.D.M.Count(x=>x.Id==id)!=1) throw new Exception("запись не применена");
			long seq1=(long)r1["seq"];
			JObject r2=s.SyncPush(JObject.Parse("{\"recs\":["+m1+"]}"));// echo — то же содержимое
			if((int)r2["applied"]!=0||(int)r2["skipped"]!=1) throw new Exception("echo не подавлен: "+r2.ToString(Newtonsoft.Json.Formatting.None));
			if((long)r2["seq"]!=seq1) throw new Exception("echo нарос в журнале");
			string m1b=m1.Replace("\"Sys\":120","\"Sys\":141");
			JObject r3=s.SyncPush(JObject.Parse("{\"recs\":["+m1b+"]}"));
			if((int)r3["applied"]!=1||s.D.M.First(x=>x.Id==id).Sys!=141) throw new Exception("LWW-правка не сработала");
			JObject pl=s.SyncPull(0,s.BinGen,0);// с начала журнала
			if(pl["reset"]!=null) throw new Exception("pull на пустом курсоре не должен reset");
			// журнал append-only: pull с начала несёт обе версии (120->141), клиент применяет по порядку — LWW
			if(pl["meas"]==null||((JArray)pl["meas"]).Count!=2) throw new Exception("pull не вернул записи: "+pl.ToString(Newtonsoft.Json.Formatting.None));
			JArray measArr=(JArray)pl["meas"];
			if(measArr[0]["Id"].ToString()!=id||measArr[1]["Id"].ToString()!=id) throw new Exception("pull не тот Id");
			if((int)measArr[0]["Sys"]!=120||(int)measArr[1]["Sys"]!=141) throw new Exception("pull не тот порядок версий (LWW)");
			JObject pl2=s.SyncPull((long)pl["seq"],s.BinGen,(long)pl["cfgRev"]);
			if(pl2["meas"]!=null) throw new Exception("pull с текущего курсора вернул лишнее");
			if(s.SyncPull(0,s.BinGen+7,0)["reset"]==null) throw new Exception("чужое поколение должно reset");
			JObject r4=s.SyncPush(JObject.Parse("{\"recs\":[{\"k\":\"measDel\",\"Id\":\""+id+"\"}]}"));
			if((int)r4["applied"]!=1||s.D.M.Any(x=>x.Id==id)) throw new Exception("тумстоун не применён");
			JObject r5=s.SyncPush(JObject.Parse("{\"recs\":[{\"k\":\"measDel\",\"Id\":\""+id+"\"}]}"));
			if((int)r5["applied"]!=0) throw new Exception("echo тумстоуна не подавлен");
			JObject pl3=s.SyncPull((long)pl["seq"],s.BinGen,(long)pl["cfgRev"]);
			if(pl3["measDel"]==null||((JArray)pl3["measDel"]).Count!=1) throw new Exception("pull не вернул тумстоун");
			Profile s2=new Profile(dir); s2.Load();// журнал пережил рестарт, удаление не «воскресло»
			if(s2.D.M.Any(x=>x.Id==id)) throw new Exception("удалённая запись воскресла после перезагрузки");
			if(!s2._dead.Contains(id)) throw new Exception("_dead не собрался из журнала");
			JObject r6=s.SyncPush(JObject.Parse("{\"recs\":[],\"cfg\":{\"NormSys\":140}}"));
			if(s.Cfg.NormSys!=140) throw new Exception("cfg не применён");
			JObject pl4=s.SyncPull((long)pl3["seq"],s.BinGen,0);
			if(pl4["cfg"]==null||(int)pl4["cfg"]["NormSys"]!=140) throw new Exception("pull не вернул cfg по ревизии");
			Directory.Delete(dir,true);
		});
		Thread.CurrentThread.CurrentCulture=savedCulture;
		Console.WriteLine(failed==0?"SELFTEST OK":"SELFTEST FAILED: "+failed);
		return failed;
	}
}
}
