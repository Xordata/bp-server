using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
// Фаза 4: серверный планировщик будильников по ВСЕМ юзерам (не только загруженным в LRU).
// Тик 30 с; время считаем в таймзоне юзера (Cfg.Tz, IANA — сервер в UTC); окно догона 30 мин
// (та же семантика, что в десктопном CheckAlarms); LastFired/dismissed персистятся в
// users/<uid>/alarms.json — рестарт сервера не плодит дубли и догоняет пропущенное за 30 мин.
static class AlarmScheduler{
	public const int CatchUpMin=30;
	public static string DataRoot;

	// должен ли будильник сработать сегодня (перенесено из Profile, семантика без изменений)
	public static bool FireToday(AlarmDef a,DateTime now){
		switch(a.Mode){
			case"days":{
				int iso=(int)now.DayOfWeek;if(iso==0)iso=7;// вс=7
				return a.Days!=null&&a.Days.Contains(iso);
			}
			case"everyN":{
				int n=Math.Max(1,a.EveryN);
				if(n<=1)return true;
				DateTime anchor;
				if(string.IsNullOrWhiteSpace(a.Anchor)||!DateTime.TryParseExact(a.Anchor,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out anchor))return true;// первый раз: срабатываем сегодня и фиксируем якорь
				return(now.Date-anchor.Date).Days%n==0;
			}
			default:return true;
		}
	}

	// Состояние будильников юзера (users/<uid>/alarms.json): что уже звенело, что звенит, что погашено.
	public class State{
		public Dictionary<string,DateTime> Fired=new Dictionary<string,DateTime>();// ключ -> момент срабатывания (UTC)
		public Dictionary<string,DateTime> MedFired=new Dictionary<string,DateTime>();
		public string RingingKey="";
		public string RingingLabel="";
		public DateTime RingingAt;
		public string DismissedKey="";
		[JsonIgnore] public string Path;
		public void Prune(){// ключи старше 2 суток не нужны
			DateTime cut=DateTime.Now.AddDays(-2);
			foreach(string k in Fired.Where(kv=>kv.Value<cut).Select(kv=>kv.Key).ToList())Fired.Remove(k);
			foreach(string k in MedFired.Where(kv=>kv.Value<cut).Select(kv=>kv.Key).ToList())MedFired.Remove(k);
		}
		public void Save(){ try{ Prune(); File.WriteAllText(Path,JsonConvert.SerializeObject(this,Formatting.Indented)); }catch{} }
		public static State Load(string path){
			State s=null;
			try{ if(File.Exists(path)) s=JsonConvert.DeserializeObject<State>(File.ReadAllText(path)); }catch{}
			s=s??new State(); s.Path=path; return s;
		}
	}

	// Чистое вычисление: какие будильники/напоминания должны сработать сейчас. Мутит state
	// (Fired/MedFired/Ringing), но файловых I/O не делает — тестопригодно.
	public class Due{ public List<string> AlarmKeys=new List<string>(); public List<string> AlarmLabels=new List<string>(); public List<string> MedKeys=new List<string>(); public List<string> MedLabels=new List<string>(); public bool AnchorsChanged; public bool Changed; }
	public static Due ComputeDue(Config cfg,State st,DateTime nowUtc){
		Due due=new Due();
		DateTime now=LocalNow(cfg.Tz,nowUtc);
		string today=now.ToString("yyyyMMdd");
		for(int i=0;i<cfg.Alarms.Count;i++){
			AlarmDef a=cfg.Alarms[i];
			if(!a.Enabled||string.IsNullOrWhiteSpace(a.Time))continue;
			string[] parts=a.Time.Split(':');
			if(parts.Length!=2)continue;
			if(!int.TryParse(parts[0],out int hh)||!int.TryParse(parts[1],out int mm))continue;
			if(hh>23||mm>59)continue;
			if(!FireToday(a,now))continue;
			string key=today+"_"+i+"_"+a.Time;
			if(st.Fired.ContainsKey(key))continue;
			DateTime target=now.Date.AddHours(hh).AddMinutes(mm);
			if(now>=target&&now-target<=TimeSpan.FromMinutes(CatchUpMin)){
				st.Fired[key]=DateTime.UtcNow;
				if(a.Mode=="everyN"&&string.IsNullOrWhiteSpace(a.Anchor)){a.Anchor=today;due.AnchorsChanged=true;}
				st.RingingKey=key; st.RingingLabel=a.Label??""; st.RingingAt=DateTime.UtcNow;
			st.DismissedKey="";// новое срабатывание гасит вчерашний dismiss
				due.AlarmKeys.Add(key); due.AlarmLabels.Add(a.Label??a.Time);
				due.Changed=true;
			}
		}
		if(cfg.MedTimes!=null){
			foreach(KeyValuePair<string,List<string>> kv in cfg.MedTimes){
				if(kv.Value==null)continue;
				string med=cfg.Meds.FirstOrDefault(x=>NormMedStatic(x)==kv.Key)??kv.Key;
				foreach(string tm in kv.Value){
					string[] parts=(tm??"").Trim().Split(':');
					if(parts.Length!=2)continue;
					if(!int.TryParse(parts[0],out int hh)||!int.TryParse(parts[1],out int mm))continue;
					if(hh>23||mm>59)continue;
					string key=today+"_md_"+kv.Key+"_"+tm;
					if(st.MedFired.ContainsKey(key))continue;
					DateTime target=now.Date.AddHours(hh).AddMinutes(mm);
					if(now>=target&&now-target<=TimeSpan.FromMinutes(CatchUpMin)){
						st.MedFired[key]=DateTime.UtcNow;
						due.MedKeys.Add(key); due.MedLabels.Add(med);
						due.Changed=true;
					}
				}
			}
		}
		return due;
	}

	public static DateTime LocalNow(string tz,DateTime nowUtc){
		try{ if(!string.IsNullOrWhiteSpace(tz)) return TimeZoneInfo.ConvertTimeFromUtc(nowUtc,TimeZoneInfo.FindSystemTimeZoneById(tz)); }catch{}
		return nowUtc;// сервер в UTC — разумный фолбэк для пустой/битой tz
	}
	static string NormMedStatic(string s){ return new string((s??"").ToLowerInvariant().Replace('ё','е').Where(char.IsLetterOrDigit).ToArray()); }

	// ---- цикл хоста ----
	public static async Task Run(CancellationToken ct){
		while(!ct.IsCancellationRequested){
			try{ await Task.Delay(30000,ct); }catch{ break; }
			try{ TickAll(); }catch(Exception ex){ Console.WriteLine("scheduler: "+ex.Message); }
		}
	}

	// один проход по всем юзерам (public для смоук-теста «догон сразу после старта»)
	public static void TickAll(){
		string usersDir=Path.Combine(DataRoot,"users");
		string[] dirs; try{ dirs=Directory.GetDirectories(usersDir); }catch{ return; }
		DateTime nowUtc=DateTime.UtcNow;
		foreach(string dir in dirs){
			try{ TickUser(dir,nowUtc); }catch(Exception ex){ Console.WriteLine("scheduler "+Path.GetFileName(dir)+": "+ex.Message); }
		}
	}

	// разбор config.json юзера (public для selftest). Replace ОБЯЗАТЕЛЕН: по умолчанию Auto
	// ДОПИСЫВАет JSON-массив к дефолтному списку из конструктора Config (ловлено на смоуке — индексы съезжали).
	public static Config LoadCfg(string path){
		try{ return JsonConvert.DeserializeObject<Config>(File.ReadAllText(path),new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace}); }catch{ return null; }
	}

	static void TickUser(string dir,DateTime nowUtc){
		string uid=Path.GetFileName(dir);
		string cfgPath=Path.Combine(dir,"config.json");
		if(!File.Exists(cfgPath))return;
		Config cfg=LoadCfg(cfgPath);
		if(cfg==null)return;
		State st=State.Load(Path.Combine(dir,"alarms.json"));
		Due due=ComputeDue(cfg,st,nowUtc);
		if(due.AnchorsChanged){// якорь everyN: пишем на диск И в LRU-профиль, иначе его следующий SaveCfg затрёт якорь старым значением
			Profile lp=null; try{ lp=UserStore.Peek(uid); }catch{}
			if(lp!=null){ lock(lp.Sync){ for(int i=0;i<cfg.Alarms.Count&&i<lp.Cfg.Alarms.Count;i++) lp.Cfg.Alarms[i].Anchor=cfg.Alarms[i].Anchor; lp.SaveCfg(); } }
			else{ try{ File.WriteAllText(cfgPath,JsonConvert.SerializeObject(cfg,Formatting.Indented)); }catch{} }
		}
		if(!due.Changed)return;
		st.Save();
		// открытые вкладки (SSE) звонят сами; push — только если ни одной вкладки нет,
		// иначе пользователь слышит и mp3 во вкладке, и push-звук одновременно (дубли)
		bool online=false; try{ online=EventHub.HasSubscribers(uid); }catch{}
		for(int i=0;i<due.AlarmKeys.Count;i++){
			try{ EventHub.Publish(uid,JObject.FromObject(new{t="alarm",key=due.AlarmKeys[i],label=due.AlarmLabels[i]}).ToString(Formatting.None)); }catch{}
		}
		for(int i=0;i<due.AlarmLabels.Count;i++)
			NotifyUser(uid,cfg,"Будильник: "+due.AlarmLabels[i],"Пора измерить давление. "+due.AlarmLabels[i],!online);
		foreach(string med in due.MedLabels)
			NotifyUser(uid,cfg,"Лекарства","Пора принять: "+med);
	}

	static void NotifyUser(string uid,Config cfg,string title,string body,bool push=true){
		bool pushed=false;
		if(push){// false = звоним открытой вкладке по SSE — push и email не плодим
			try{ var t=PushStore.SendToUser(uid,title,body); if(t.Wait(TimeSpan.FromSeconds(25))) pushed=t.Result; }// Wait(таймаут) = «успел ли», НЕ результат; Result берём только после Wait (зависший push-эндпоинт не блокирует цикл)
			catch(Exception ex){ Console.WriteLine("push "+uid+": "+ex.Message); }
		}
		if(push&&!pushed&&cfg.EmailFallback&&!string.IsNullOrWhiteSpace(Program.ACfg.SmtpUser)){
			string to=string.IsNullOrWhiteSpace(cfg.EmailTo)?uid:cfg.EmailTo;
			try{ SmtpSender.Send(Program.ACfg,to,title,body); }catch(Exception ex){ Console.WriteLine("email "+uid+": "+ex.Message); }
		}
	}
}
}
