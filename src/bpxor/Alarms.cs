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
// Будильники и напоминания о лекарствах (логика расписания; доставка — Фаза 4) (порт BpMonitor.cs)
static partial class App
{
	// ----------------------------- будильник -----------------------------
	static readonly HashSet<string> Fired=new HashSet<string>();
	static readonly HashSet<string> Announced=new HashSet<string>();
	// должен ли будильник сработать сегодня с учётом расписания
	static bool FireToday(AlarmDef a,DateTime now){
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
	public static void CheckAlarms(){
		DateTime now=DateTime.Now;
		string today=now.ToString("yyyyMMdd");
		Fired.RemoveWhere(k=>!k.StartsWith(today));// чистим вчерашние ключи
		Announced.RemoveWhere(k=>!k.StartsWith(today));
		for(int i=0;i<Cfg.Alarms.Count;i++){
			AlarmDef a=Cfg.Alarms[i];
			if(!a.Enabled||string.IsNullOrWhiteSpace(a.Time))continue;
			string[] parts=a.Time.Split(':');
			if(parts.Length!=2)continue;
			if(!int.TryParse(parts[0],out int hh)||!int.TryParse(parts[1],out int mm))continue;
			if(!FireToday(a,now))continue;
			string key=today+"_"+i+"_"+a.Time;
			DateTime target=now.Date.AddHours(hh).AddMinutes(mm);
			// догоняем пропущенные (сон/лаги): окно 30 минут после срабатывания
			if(!Fired.Contains(key)&&now>=target&&now-target<=TimeSpan.FromMinutes(30)){
				Fired.Add(key);
				if(a.Mode=="everyN"&&string.IsNullOrWhiteSpace(a.Anchor)){a.Anchor=today;try{SaveCfg();}catch{}}// фиксируем старт цикла «каждые N дней»
				StartAlarm();
				Notify("BpMonitor","Будильник: "+(a.Label??a.Time));
			}
			if(Cfg.AlarmAheadMin<=0)continue;
			DateTime ahead=target-TimeSpan.FromMinutes(Cfg.AlarmAheadMin);
			if(!Announced.Contains(key)&&now>=ahead&&now<target){
				Announced.Add(key);
				Notify("BpMonitor","Через "+Cfg.AlarmAheadMin+" мин будильник: "+(a.Label??a.Time));
			}
		}
	}

	// ----------------------------- напоминания о приёме лекарств -----------------------------
	static readonly HashSet<string> MedFired=new HashSet<string>();
	public static void CheckMedReminders(){
		if(Cfg.MedTimes==null||Cfg.MedTimes.Count==0) return;
		DateTime now=DateTime.Now;
		string today=now.ToString("yyyyMMdd");
		MedFired.RemoveWhere(k=>!k.StartsWith(today));
		foreach(KeyValuePair<string,List<string>> kv in Cfg.MedTimes){
			if(kv.Value==null) continue;
			string med=Cfg.Meds.FirstOrDefault(x=>NormMed(x)==kv.Key)??kv.Key;
			foreach(string tm in kv.Value){
				string[] parts=(tm??"").Trim().Split(':');
				if(parts.Length!=2) continue;
				if(!int.TryParse(parts[0],out int hh)||!int.TryParse(parts[1],out int mm)) continue;
				if(hh>23||mm>59) continue;
				string key=today+"_md_"+kv.Key+"_"+tm;
				if(MedFired.Contains(key)) continue;
				DateTime target=now.Date.AddHours(hh).AddMinutes(mm);
				if(now>=target&&now-target<=TimeSpan.FromMinutes(30)){// окно догона, как у будильников
					MedFired.Add(key);
					Notify("BpMonitor — лекарства","Пора принять: "+med+". Отметьте приём во вкладке «Лекарства».");
				}
			}
		}
	}
}
}
