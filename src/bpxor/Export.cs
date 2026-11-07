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
using MiniExcelLibs;

namespace BpMonitor
{
// Экспорт CSV/XLSX, отчёт врача, импорт сна, перенос всего одним JSON
partial class Profile
{
	// ----------------------------- экспорт -----------------------------
	string CsvLine(params object[] vals){
		return string.Join(";",vals.Select(v=>{
			string s=v==null?"":Convert.ToString(v,CultureInfo.InvariantCulture)?.Replace("\"","\"\"");
			return s!=null&&(s.Contains(";")||s.Contains("\"")||s.Contains("\n"))?"\""+s+"\"":s;
		}));
	}
	private int? JInt(JObject f,string key){
		if(f[key]==null) return null;
		return int.TryParse(f[key].ToString().Trim(),out int i)?(int?)i:null;
	}
	// JToken -> DateTime: Newtonsoft сам превращает ISO-строки с секундами в Date JValue,
	// а .ToString() у него идёт через текущую культуру (ru: "11.06.2026") -> инвариантный парсер меняет день/месяц
	public DateTime JT(JObject jo,string key){
		JToken t=jo[key];
		if(t==null) throw new FormatException("нет поля "+key);
		if(t.Type==JTokenType.Date) return (DateTime)t;
		return DateTime.Parse(t.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.None);
	}
	DateTime? JDate(JObject f,string key){
		if(f[key]==null) return null;
		string s=f[key].ToString().Trim();
		if(s=="") return null;
		DateTime d;
		return DateTime.TryParse(s,out d)?(DateTime?)d:null;
	}
	public List<Measurement> Filtered(JObject f){
		List<Measurement> list=D.M.ToList()??throw new ArgumentNullException("D.M.ToList()");
		DateTime? from=JDate(f,"from");
		DateTime? to=JDate(f,"to");
		if(from!=null) list=list.Where(m=>m.T>=from.Value).ToList();
		if(to!=null) list=list.Where(m=>m.T<=to.Value.AddSeconds(86399)).ToList();
		int? sm=JInt(f,"sysMin");
		int? dm=JInt(f,"diaMin");
		int? pm=JInt(f,"pulseMin");
		if(sm!=null) list=list.Where(m=>m.Sys>=sm).ToList();
		if(dm!=null) list=list.Where(m=>m.Dia>=dm).ToList();
		if(pm!=null) list=list.Where(m=>m.Pulse>=pm).ToList();
		if(f["onlyMeds"]!=null&&(bool)f["onlyMeds"]) list=list.Where(m=>m.Meds.Count>0).ToList();
		return list.OrderBy(m=>m.T).ToList();// в отчётах — старые сверху
	}
	List<Dictionary<string,object>> DoctorRows(List<Measurement> list){
		List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
		int maxPerDay=list.GroupBy(m=>m.T.Date).Select(g=>g.Count()).DefaultIfEmpty(0).Max();
		maxPerDay=Math.Min(Math.Max(maxPerDay,6),8);
		foreach(var day in list.GroupBy(m=>m.T.Date).OrderBy(g=>g.Key)){
			List<Measurement> ms=day.OrderBy(m=>m.T).ToList();
			Dictionary<string,object> r=new Dictionary<string,object>{{"Дата",day.Key.ToString("dd.MM.yyyy")}};
			for(int i=1;i<=maxPerDay;i++){
				Measurement m=ms.Count>=i?ms[i-1]:null;
				r["Изм."+i]=m==null?"":$"{m.T:HH:mm} — {m.Sys}/{m.Dia}, п:{m.Pulse}";
			}
			// приём лекарств: union журнала отметок и лекарств, отмеченных на измерении
			List<string> takes=new List<string>();
			foreach(MedTakeEntry mt in D.MedTakes.Where(x=>x.T.Date==day.Key)) takes.Add($"{mt.T:HH:mm} {mt.Med}");
			foreach(Measurement m in ms) foreach(string md in m.Meds) takes.Add($"{m.T:HH:mm} {md}");
			r["Приём лекарств"]=string.Join("; ",takes.OrderBy(s=>s).Distinct());
			r["Kp (макс)"]=ms.Any(m=>m.Kp.HasValue)?((ms.Max(m=>m.Kp??0)/10.0).ToString("0.0",CultureInfo.InvariantCulture)):"";
			r["Эмоции"]=string.Join("; ",ms.Where(m=>m.EmotionV.HasValue).Select(m=>$"{m.T:HH:mm} {m.EmotionLabel}").Distinct());
			r["Шум/события"]=string.Join("; ",ms.SelectMany(m=>m.Noise).Distinct());
			// сон: из журнала за этот день (бывает и в дни без измерений), иначе снимок с измерений
			List<SleepEntry> sl=D.S.Where(s=>s.T.Date==day.Key).OrderBy(s=>s.T).ToList();
			r["Сон"]=sl.Count>0?string.Join("; ",sl.Select(s=>$"{s.Hours:0.#} ч (кач. {s.Quality})"))
				:(ms.Any(m=>m.SleepH.HasValue)?$"{ms.Min(m=>m.SleepH??0):0.#} ч (кач. {ms.Max(m=>m.SleepQ??0)})":"");
			rows.Add(r);
		}
		return rows;
	}
	List<Dictionary<string,object>> RowRows(List<Measurement> list){
		List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
		foreach(Measurement m in list)
			rows.Add(new Dictionary<string,object>{
				{"Дата",m.T.ToString("dd.MM.yyyy")},{"Время",m.T.ToString("HH:mm")},
				{"Сист.",m.Sys},{"Диаст.",m.Dia},{"Пульс",m.Pulse},
				{"Давл. мм",m.PressMm?.ToString("0")??""},{"Темп. °C",m.TempC?.ToString("0")??""},
				{"Влажн. %",m.Hum?.ToString("0")??""},{"Kp",m.Kp!=null?(m.Kp.Value/10.0).ToString("0.0",CultureInfo.InvariantCulture):""},{"Погода",m.Wx},
				{"Лекарства",string.Join(", ",m.Meds)},{"Эмоция",m.EmotionLabel},
				{"Шум",string.Join("; ",m.Noise)},{"Сон ч",m.SleepH?.ToString("0.0")??""},
				{"Сон кач.",m.SleepQ?.ToString()??""},{"Прим.",m.Note}
			});
		return rows;
	}
	List<Dictionary<string,object>> DayRows(List<Measurement> list){
		List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
		foreach(IGrouping<DateTime,Measurement> day in list.GroupBy(m=>m.T.Date).OrderBy(g=>g.Key)){
			List<Measurement> ms=day.ToList();
			rows.Add(new Dictionary<string,object>{
				{"Дата",day.Key.ToString("dd.MM.yyyy")},{"Кол-во",ms.Count},
				{"Сред. сист.",Math.Round(ms.Average(m=>m.Sys),1)},{"Сред. диаст.",Math.Round(ms.Average(m=>m.Dia),1)},
				{"Сред. пульс",Math.Round(ms.Average(m=>m.Pulse),1)},
				{"Давл. мм",ms.Any(m=>m.PressMm.HasValue)?string.Format("{0}–{1}",ms.Min(m=>m.PressMm??0),ms.Max(m=>m.PressMm??0)):""},
				{"Темп. °C",ms.Any(m=>m.TempC.HasValue)?string.Format("{0}–{1}",ms.Min(m=>m.TempC??0),ms.Max(m=>m.TempC??0)):""},
				{"Kp макс",ms.Any(m=>m.Kp.HasValue)?((ms.Max(m=>m.Kp??0)/10.0).ToString("0.0",CultureInfo.InvariantCulture)):""},
				{"Лекарства",string.Join(", ",ms.SelectMany(m=>m.Meds).Distinct())},
				{"Эмоции",string.Join("; ",ms.Where(m=>m.EmotionLabel!="").Select(m=>m.EmotionLabel).Distinct())},
				{"Шум",string.Join("; ",ms.SelectMany(m=>m.Noise).Distinct())},
				{"Сон ч",ms.Any(m=>m.SleepH.HasValue)?ms.Min(m=>m.SleepH??0).ToString("0.0"):""}
			});
		}
		return rows;
	}
	public byte[] BuildExport(JObject f,string kind,string fmt,out string filename,out string ctype){
		ctype="text/csv";
		List<Measurement> list=Filtered(f);
		DateTime from=list.Count>0?list.First().T:DateTime.Now.AddDays(-7);
		DateTime to=list.Count>0?list.Last().T:DateTime.Now;
		string tag=from.ToString("yyyyMMdd")+"_"+to.ToString("yyyyMMdd");
		if(fmt=="xlsx"){
			List<Dictionary<string,object>> rows=kind=="doctor"?DoctorRows(list):
				kind=="days"?DayRows(list):RowRows(list);
			string name="bp_"+kind+"_"+tag+".xlsx";
			string p=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+"_"+name);
			MiniExcel.SaveAs(p,rows);
			byte[] b=File.ReadAllBytes(p);
			File.Delete(p);
			filename=name;
			ctype="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
			return b;
		}
		filename="bp_"+kind+"_"+tag+".csv";
		StringBuilder sb=new StringBuilder("\uFEFF");
		switch(kind){
			case"doctor":{
				List<Dictionary<string,object>> dr=DoctorRows(list);
				if(dr.Count>0){
					sb.AppendLine(CsvLine(dr.First().Keys.ToArray<object>()));
					foreach(var r in dr) sb.AppendLine(CsvLine(r.Values.ToArray()));
				}
				break;
			}
			case"days":{
				List<Dictionary<string,object>> dr=DayRows(list);
				if(dr.Count>0){
					sb.AppendLine(CsvLine(dr.First().Keys.ToArray()));
					foreach(var r in dr) sb.AppendLine(CsvLine(r.Values.ToArray()));
				}
				break;
			}
			default:{
				sb.AppendLine(CsvLine("Дата","Время","Сист","Диаст","Пульс","Давл.мм","Темп.°C","Влажн.%","Kp","Погода","Лекарства","Эмоция","Шум","Сон.ч","Прим."));
				foreach(var m in list)
					sb.AppendLine(CsvLine(m.T.ToString("dd.MM.yyyy"),m.T.ToString("HH:mm"),m.Sys,m.Dia,m.Pulse,
					m.PressMm?.ToString("0")??"",m.TempC?.ToString("0")??"",m.Hum?.ToString("0")??"",
					m.Kp?.ToString()??"",m.Wx,string.Join(", ",m.Meds),m.EmotionLabel,
					string.Join("; ",m.Noise),m.SleepH?.ToString("0.0")??"",m.Note));
				break;
			}
		}
		return Encoding.UTF8.GetBytes(sb.ToString());
	}

	// ----------------------------- импорт сна (Mi Band / Mi Fit CSV) -----------------------------
	public int ImportSleepCsv(string text,out string err,List<SleepEntry> addedList=null){
		err="";
		int added=0;
		Regex dateRe=new Regex(@"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})|(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})");
		foreach(string rawLine in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){
			string line=rawLine.Trim();
			if(line=="") continue;
			if(line.IndexOfAny(new[]{'д','Д','a','A'})>=0&&
				(line.IndexOf("date",StringComparison.OrdinalIgnoreCase)>=0||line.IndexOf("дата",StringComparison.OrdinalIgnoreCase)>=0)) continue;// шапка
			Match dm=dateRe.Match(line);
			if(!dm.Success) continue;
			int y,mo,dd;
			if(dm.Groups[1].Success){
				y=int.Parse(dm.Groups[1].Value);
				mo=int.Parse(dm.Groups[2].Value);
				dd=int.Parse(dm.Groups[3].Value);
			}
			else{
				dd=int.Parse(dm.Groups[4].Value);
				mo=int.Parse(dm.Groups[5].Value);
				y=int.Parse(dm.Groups[6].Value);
			}
			if(y<2000) y+=2000;
			DateTime dt;
			try{ dt=new DateTime(y,mo,dd); } catch{ continue; }
			List<string> tokens=line.Split(new[]{',',';','\t','|'},StringSplitOptions.RemoveEmptyEntries).Select(t=>t.Trim().Trim('"')).ToList();
			tokens.RemoveAll(t=>dateRe.IsMatch(t));
			float? hours=null;
			int? qual=null;
			foreach(var t in tokens){
				double v;
				if(t.Contains(":"))// 7:30
				{
					var hp=t.Split(':');
					if(hp.Length==2&&int.TryParse(hp[0],out var hh)&&int.TryParse(hp[1],out var mi)&&hh<=24){
						hours=hh+mi/60f;
						continue;
					}
				}
				if(!double.TryParse(t,NumberStyles.Any,CultureInfo.InvariantCulture,out v)) continue;
				if(hours==null) hours=v>24?(float)(v/60.0):(float)v;// минуты -> часы
				else if(qual==null) qual=v>10?(int)Math.Round(v/10):(int)Math.Round(v);
			}
			if(hours==null||hours.Value<0.2f||hours.Value>24f) continue;
			SleepEntry se=new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=dt,Hours=hours.Value,Quality=qual??5};
			D.S.Add(se);
			if(addedList!=null) addedList.Add(se);
			added++;
		}
		return added;
	}

	// ----------------------------- перенос: экспорт/импорт всего одним файлом -----------------------------
	// Единый JSON-снимок: настройки + все данные + веса НН + справочник ФК.
	// GET /api/export/all скачивает его, POST /api/import/all восстанавливает (с автобэкапом перед заменой).
	public JObject ExportAll(){
		return new JObject{
			["app"]="BpMonitor",
			["ver"]=1,
			["exported"]=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
			["cfg"]=JObject.FromObject(Cfg),
			["data"]=new JObject{
				["measurements"]=JArray.FromObject(D.M.OrderBy(m=>m.T).ToList()),
				["emotions"]=JArray.FromObject(D.E.OrderBy(e=>e.T).ToList()),
				["noises"]=JArray.FromObject(D.N.OrderBy(e=>e.T).ToList()),
				["sleeps"]=JArray.FromObject(D.S.OrderBy(e=>e.T).ToList()),
				["salts"]=JArray.FromObject(D.Salt.OrderBy(e=>e.T).ToList()),
				["medtakes"]=JArray.FromObject(D.MedTakes.OrderBy(x=>x.T).ToList()),
				["lastPopup"]=D.LastPopup!=null?(JToken)D.LastPopup.Value.ToString("yyyy-MM-dd HH:mm:ss"):JValue.CreateNull()
			},
			["nn"]=D.Nn!=null?JObject.FromObject(D.Nn):null,
			["medpk"]=JObject.FromObject(MedPkDb)
		};
	}
	public string ImportAll(JObject j){
		if((string)j["app"]!="BpMonitor") throw new Exception("это не файл экспорта BpMonitor");
		int ver=(int?)j["ver"]??0;
		if(ver<1) throw new Exception("неподдерживаемая версия файла: "+ver);
		JObject c=j["cfg"] as JObject,d=j["data"] as JObject,nn=j["nn"] as JObject;
		if(d==null) throw new Exception("в файле нет блока data");
		Config nc=JsonConvert.DeserializeObject<Config>(c!=null?c.ToString():"{}",JSONSettings)??new Config();
		nc.Meds=(nc.Meds??new List<string>()).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		nc.Noises=(nc.Noises??new List<string>()).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if(nc.MedKt==null) nc.MedKt=new Dictionary<string,MedKt>();
		if(nc.MedTimes==null) nc.MedTimes=new Dictionary<string,List<string>>();
		DataStore nd=new DataStore();
		if(d["measurements"] is JArray a1) nd.M=a1.ToObject<List<Measurement>>()??new List<Measurement>();
		if(d["emotions"] is JArray a2) nd.E=a2.ToObject<List<EmotionEntry>>()??new List<EmotionEntry>();
		if(d["noises"] is JArray a3) nd.N=a3.ToObject<List<NoiseEntry>>()??new List<NoiseEntry>();
		if(d["sleeps"] is JArray a4) nd.S=a4.ToObject<List<SleepEntry>>()??new List<SleepEntry>();
		if(d["salts"] is JArray a6) nd.Salt=a6.ToObject<List<SaltEntry>>()??new List<SaltEntry>();
		if(d["medtakes"] is JArray a5) nd.MedTakes=a5.ToObject<List<MedTakeEntry>>()??new List<MedTakeEntry>();
		System.Func<string,bool> goodId=s=>{ Guid g; return Guid.TryParse(s,out g); };// Id обязан быть Guid: кривые строки ломают UI (onclick-инъекции)
		nd.M=nd.M.Where(x=>goodId(x.Id)).ToList(); nd.E=nd.E.Where(x=>goodId(x.Id)).ToList(); nd.N=nd.N.Where(x=>goodId(x.Id)).ToList(); nd.S=nd.S.Where(x=>goodId(x.Id)).ToList(); nd.MedTakes=nd.MedTakes.Where(x=>goodId(x.Id)).ToList();
		if(d["lastPopup"]!=null&&d["lastPopup"].Type!=JTokenType.Null){
			DateTime lpd;
			if(DateTime.TryParse((string)d["lastPopup"],CultureInfo.InvariantCulture,DateTimeStyles.None,out lpd)) nd.LastPopup=lpd;
		}
		NnModel nnd=null;
		if(nn!=null){ try{ nnd=nn.ToObject<NnModel>(); } catch{} }
		if(nnd==null||nnd.W1==null||nnd.W1b==null||nnd.W2==null||nnd.B2==null)
			throw new Exception("в файле нет корректной модели НН — импорт отменён, данные не тронуты");
		// страховка перед заменой: архив текущего состояния
		try{
			string dir=Path.Combine(ExeDir,"backups");
			Directory.CreateDirectory(dir);
			string zip=Path.Combine(dir,"preimport-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip");
			using(ZipArchive z=new ZipArchive(File.Create(zip),ZipArchiveMode.Create)){
				if(File.Exists(CfgPath)) z.CreateEntryFromFile(CfgPath,"config.json");
				if(File.Exists(DataBinPath)) z.CreateEntryFromFile(DataBinPath,"data.bin");
				string nnp=Path.Combine(ExeDir,"data","nn.bin");
				if(File.Exists(nnp)) z.CreateEntryFromFile(nnp,"nn.bin");
			}
			Log("import: preimport backup "+Path.GetFileName(zip));
		} catch(Exception ex){ Log("preimport backup: "+ex.Message); }
		Cfg=nc; D=nd; D.Nn=nnd;
		if(j["medpk"] is JObject mp){
			Dictionary<string,double[]> md=new Dictionary<string,double[]>();
			foreach(KeyValuePair<string,JToken> kv in mp){
				JArray a=kv.Value as JArray; if(a==null||a.Count<2) continue;
				string k=NormMed(kv.Key); if(k=="") continue;
				double h=(double?)a[0]??0,dd=(double?)a[1]??0;
				if(h>0&&dd>0) md[k]=new[]{h,dd};
			}
			if(md.Count>0){ MedPkDb=md; SaveMedPk(); }
		}
		Save();// config.json + полная перезапись data.bin + SaveNn
		EnsureNnDim();// размеры/лекарства из файла могут не совпадать -> пересборка размерности
		return "измерений: "+D.M.Count+", эмоций: "+D.E.Count+", шумов: "+D.N.Count+", сна: "+D.S.Count+", приёмов: "+D.MedTakes.Count;
	}
}
}
