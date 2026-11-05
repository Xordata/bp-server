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
// Ядро: состояние, лог, фоновые задачи (порт BpMonitor.cs)
partial class Profile
{
	private string ExeDir,CfgPath,DataPath;
	public string DataBinPath,IndexPath;
	public string ExeDirPath=>ExeDir;
	public string Uid="";// заполняет UserStore при регистрации профиля
	public DateTime LastPoll;// фоновый интервал PollWeather per-user
	public Profile(string dir){ InitNotify(); InitPaths(dir); }
	public Config Cfg=new Config();
	public DataStore D=new DataStore();
	HttpClient Http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};
	readonly object Io=new object();
	public readonly object Sync=new object();// вся мутация D/Cfg — только под этим замком
	public const string TokenMask="********";
	// Файловый лог: data\logs\yyyyMMdd.log (ротация при >5 МБ). Молчаливые catch{} скрывали
	// сетевые сбои — «почему не обновилась погода» расследовалось вручную (кейс августа 2026).
	readonly object LogIo=new object();
	public void Log(string msg){
		try{
			lock(LogIo){
				string dir=Path.Combine(ExeDir,"data","logs");
				Directory.CreateDirectory(dir);
				string f=Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd")+".log");
				try{ FileInfo fi=new FileInfo(f); if(fi.Exists&&fi.Length>5*1024*1024){ File.Move(f,f+".old"); } }catch{}
				File.AppendAllText(f,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+msg+"\r\n",Encoding.UTF8);
			}
		} catch{}
	}
	// Фоновые задачи (GA-обучение, бэкфилл погоды): статус читается UI через GET /api/job,
	// прогресс переживает закрытие вкладки.
	public class Job{
		public string Name;
		public DateTime Started;
		public int Done,Total;
		public string Result;
		public string Error;
		public bool Running;
	}
	Job _job;
	readonly object JobLock=new object();
	public bool TryStartJob(string name,int total=0){
		lock(JobLock){
			if(_job!=null&&_job.Running) return false;
			_job=new Job{Name=name,Started=DateTime.Now,Total=total,Running=true};
			return true;
		}
	}
	void JobProgress(int done,int total=-1){
		Job j=_job; if(j==null||!j.Running) return;
		j.Done=done; if(total>=0) j.Total=total;
	}
	public string CurrentJobName{ get{ lock(JobLock){ return _job!=null?_job.Name:null; } } }
	public string JobResult{ get{ Job j=_job; return j!=null?j.Result:null; } set{ Job j=_job; if(j!=null) j.Result=value; } }
	public string JobError{ set{ Job j=_job; if(j!=null) j.Error=value; } }
	public void FinishJob(){ Job j=_job; if(j!=null) j.Running=false; }
	public JObject JobJson(){
		Job j=_job;
		if(j==null) return new JObject{["running"]=false};
		return new JObject{["running"]=j.Running,["name"]=j.Name,["started"]=j.Started.ToString("yyyy-MM-dd HH:mm:ss"),["done"]=j.Done,["total"]=j.Total,["result"]=j.Result??"",["error"]=j.Error??""};
	}
	double Clamp(double v,double lo,double hi){ return v<lo?lo:(v>hi?hi:v); }
	public readonly string[] FeatBase={
		"час sin","час cos","день sin","день cos","давл. мм","температура","влажность","Kp",
		"эмоция","шум(кол-во)","сон ч","сон кач","лекарств(кол-во)","возраст","пол"
	};
	// полные имена фич: базовые + по 2 на каждое лекарство (активность/окно)
	public string[] GetFeatNames(){
		List<string> l=new List<string>(FeatBase);
		foreach(string med in Cfg.Meds){l.Add(med+": активность");l.Add(med+": окно");}
		return l.ToArray();
	}
	// размерность входа НН зависит от списка лекарств
	public int InDim(){ return FeatBase.Length+2*Cfg.Meds.Count; }
	public class Wx{
		public float? PressMm,TempC,Hum;
		public int? Kp;
		public string Text="";
		public DateTime? Updated;
	}
	public Wx Weather=new Wx();
	// Прогноз: open-meteo hourly (почасово, ближайшие 2 суток) + NOAA SWPC Kp-прогноз
	// (3-часовые бины, UTC; entries observed!="observed"). Используется для popup «заранее».
	public class WxHour{
		public DateTime T;
		public double? Press,Temp,Hum;
		public int? Code;
	}
	List<WxHour> Forecast=new List<WxHour>();
	DateTime? ForecastUpdated;
	List<KeyValuePair<DateTime,double>> KpForecast=new List<KeyValuePair<DateTime,double>>();// старт 3-ч бина (UTC) -> Kp
	bool _weatherBusy;
	readonly JsonSerializerSettings JSONSettings=new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace};

	public void Snapshot(Measurement m){
		// кэш старше 2*интервала опроса считаем протухшим: пишем пустоту и обновляем погоду на будущее
		bool stale=Weather.Updated==null||DateTime.Now-Weather.Updated.Value>TimeSpan.FromMinutes(Math.Max(1,Cfg.PollMin)*2);
		if(stale){ m.PressMm=null;m.TempC=null;m.Hum=null;m.Kp=null;m.Wx="";PollWeather(); }
		else{ m.PressMm=Weather.PressMm;m.TempC=Weather.TempC;m.Hum=Weather.Hum;m.Kp=Weather.Kp;m.Wx=Weather.Text; }
		SnapshotContext(m);
	}
	// контекст (эмоция/шум/сон) с окном +10 мин после измерения — вызывается и при добавлении записей журнала
	public void SnapshotContext(Measurement m){
		EmotionEntry e=D.E.Where(x=>x.T<=m.T.AddMinutes(10)&&m.T-x.T<=TimeSpan.FromHours(12)).OrderByDescending(x=>x.T).FirstOrDefault();
		m.EmotionV=e==null?(float?)null:e.Val;
		m.EmotionLabel=e==null?"":DataStore.EmotionLabel(e.Val);
		m.Noise=D.N.Where(x=>x.T<=m.T.AddMinutes(10)&&m.T-x.T<=TimeSpan.FromHours(3)).Select(x=>x.Type).Distinct().ToList();
		SleepEntry s=D.S.Where(x=>x.T<=m.T.AddMinutes(10)&&m.T-x.T<=TimeSpan.FromHours(36)).OrderByDescending(x=>x.T).FirstOrDefault();
		m.SleepH=s?.Hours;
		m.SleepQ=s?.Quality;
	}

	// ----------------------------- заглушки веба (вместо WinForms/NAudio) -----------------------------
	// Канал уведомлений: в v1 лог; Фаза 4 подключит Web Push / email.
	public Action<string,string> Notify;
	void InitNotify(){ Notify=(title,msg)=>Log("NOTIFY "+title+": "+(msg??"").Replace("\r\n"," | ")); }
	bool _alarmRinging;
	public bool AlarmRinging=>_alarmRinging;
	public void StartAlarm(){ _alarmRinging=true; }   // звук будильника живёт в браузере (Фаза 4)
	public void StopAlarm(){ _alarmRinging=false; }
	public bool GetAutostart()=>false;                 // автозапуск реестра — десктопная история
	public void SetAutostart(bool on){}
}
}
