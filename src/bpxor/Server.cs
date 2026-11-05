// Фаза 3: серверные общие ресурсы — кэш погоды (делят все юзеры) и глобальная очередь
// фоновых задач (2 vCPU: тяжёлые train/tune/validate не должны идти параллельно).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace BpMonitor{

// Общий кэш внешних API (open-meteo/GFZ/NOAA): экономия трафика и лимитов сервера.
// Ключевание: live/прогноз-arc — по округлённым координатам (~1 км), Kp/NOAA — глобально.
static class WxCache{
	public const int LiveTtlMin=10;      // open-meteo current+hourly
	public const int KpTtlMin=60;       // GFZ Kp live (3-ч бины)
	public const int KpFcTtlMin=180;    // NOAA SWPC прогноз Kp
	public const int RecentDayTtlMin=360;// архивные сутки за последние ~5 дней (ERA5 догоняет)
	const int Cap=4096;
	public static string Key(double lat,double lon){
		return Math.Round(lat,2).ToString("0.00",CultureInfo.InvariantCulture)+","+Math.Round(lon,2).ToString("0.00",CultureInfo.InvariantCulture);
	}
	class Entry{ public object Val; public DateTime At; }
	static readonly ConcurrentDictionary<string,Entry> _e=new ConcurrentDictionary<string,Entry>();
	// ttlMin<0 = бессрочно (старые архивные сутки неизменны)
	public static bool Try<T>(string key,int ttlMin,out T val){
		val=default(T);
		Entry en; if(!_e.TryGetValue(key,out en)) return false;
		if(ttlMin>=0&&(DateTime.Now-en.At).TotalMinutes>ttlMin) return false;
		if(!(en.Val is T)) return false;
		val=(T)en.Val; return true;
	}
	public static void Put<T>(string key,T val){
		_e[key]=new Entry{Val=val,At=DateTime.Now};
		if(_e.Count>Cap) Sweep();
	}
	static void Sweep(){
		Entry tmp;
		foreach(KeyValuePair<string,Entry> kv in _e.ToArray()) if((DateTime.Now-kv.Value.At).TotalHours>24) _e.TryRemove(kv.Key,out tmp);
	}
	public static int Count{ get{ return _e.Count; } }
	public static void Clear(){ _e.Clear(); }
}

// Глобальная очередь фоновых задач: не более Max одновременно (остальные ждут FIFO).
// Per-user «уже выполняется» остаётся как был — очередь добавляет серверный лимит сверху.
static class JobGate{
	public static int Max=1;
	static readonly object Lk=new object();
	static readonly Queue<Action> _q=new Queue<Action>();
	static int _running;
	// true = запущена сразу; false = встала в очередь (onQueued вызывается в этом случае)
	public static bool Run(Action job,Action onQueued=null){
		bool started;
		lock(Lk){
			if(_running<Max){ _running++; started=true; }
			else{ _q.Enqueue(job); if(onQueued!=null) onQueued(); started=false; }
		}
		if(started) Task.Run(()=>{ try{ job(); } finally{ Release(); } });
		return started;
	}
	static void Release(){
		Action next=null;
		lock(Lk){
			_running--;
			if(_q.Count>0&&_running<Max){ _running++; next=_q.Dequeue(); }
		}
		if(next!=null){ Action n=next; Task.Run(()=>{ try{ n(); } finally{ Release(); } }); }
	}
	public static int Pending{ get{ lock(Lk) return _q.Count; } }
	public static int Running{ get{ lock(Lk) return _running; } }
	public static void Reset(){ lock(Lk){ _q.Clear(); _running=0; } }
}
}
