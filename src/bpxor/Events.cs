// Фаза 5: шина событий для SSE /api/events. Мутации (Post, конец PollWeatherAsync,
// FinishJob, dismiss) публикуют короткий JSON подписчикам вкладки; SSE-хэндлер пишет его в
// поток. Каналы unbounded: медленный клиент копит, при отключении вычищается finally.
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;

namespace BpMonitor{
static class EventHub{
	static readonly ConcurrentDictionary<string,List<Channel<string>>> Subs=new ConcurrentDictionary<string,List<Channel<string>>>();
	public static Channel<string> Subscribe(string uid){
		Channel<string> ch=Channel.CreateUnbounded<string>();
		lock(Subs){
			List<Channel<string>> l;
			if(!Subs.TryGetValue(uid,out l)){ l=new List<Channel<string>>(); Subs[uid]=l; }
			l.Add(ch);
		}
		return ch;
	}
	public static void Unsubscribe(string uid,Channel<string> ch){
		lock(Subs){
			List<Channel<string>> l;
			if(Subs.TryGetValue(uid,out l)){ l.Remove(ch); if(l.Count==0) Subs.TryRemove(uid,out l); }
		}
	}
	public static bool HasSubscribers(string uid){ lock(Subs){ return Subs.TryGetValue(uid,out List<Channel<string>> l)&&l.Count>0; } }
	public static void Publish(string uid,string json){
		List<Channel<string>> l;
		if(!Subs.TryGetValue(uid,out l)) return;
		Channel<string>[] arr; lock(Subs){ arr=l.ToArray(); }
		foreach(Channel<string> ch in arr) ch.Writer.TryWrite(json);
	}
}
}
