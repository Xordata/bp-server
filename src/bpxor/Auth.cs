// Фаза 2: авторизация IMAP-пробом maddy, серверные сессии, реестр профилей (cap 50).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{

public class AuthCfg{
	public string AuthHost="127.0.0.1";// maddy на этом же сервере; для удалённой разработки можно переопределить
	public int AuthPort=993;
	public string Domain="xor.world";
	public int MaxUsers=50;
	public int FailDelaySec=5;
	public int SessionDays=30;
	public int Port=8090;
	public bool SecureCookies=true;// false — только для локальной разработки по http: куки срабатывают без TLS
	public int MaxConcurrentJobs=1;// глобальная очередь фоновых задач (решение: 2 vCPU — тяжёлые job'ы последовательно)
	public string DataRoot="";// заполняет Main (VAPID/PushStore)
	public string SmtpHost="127.0.0.1";// maddy submission на этом же сервере
	public int SmtpPort=587;
	public string SmtpUser="";// адрес ящика-бота (только в bpxor.config.json рядом с exe); пусто = email-фолбэк выключен
	public string SmtpPass="";
	public string SmtpFrom="";// пусто = From берётся из SmtpUser (адрес бота не хранится в коде)
	public string WxSource="openmeteo";// "metno" — api.met.no (open-meteo лежит на Hetzner, который режет входящие с некоторых хостинговых ASN)
}

static class MaddyAuth{
	// Тот же IMAP LOGIN probe, что в maddy_add_mbox.sh: TLS на 993, LOGIN, ждём "OK ... LOGIN completed".
	public static async Task<bool> Verify(string host,int port,string login,string password){
		try{
			using(TcpClient tcp=new TcpClient()){
				var connect=tcp.ConnectAsync(host,port);
				if(await Task.WhenAny(connect,Task.Delay(10000))!=connect) return false;
				await connect;
				using(SslStream ssl=new SslStream(tcp.GetStream(),false,(s,c,ch,e)=>true)){
					await ssl.AuthenticateAsClientAsync(host);
					using(StreamReader rd=new StreamReader(ssl,Encoding.ASCII))
					using(StreamWriter wr=new StreamWriter(ssl,new UTF8Encoding(false)){AutoFlush=true}){
						await rd.ReadLineAsync();// приветствие
						string esc(string s){ return s.Replace("\\","\\\\").Replace("\"","\\\""); }
						await wr.WriteLineAsync("A1 LOGIN \"" + esc(login) + "\" \"" + esc(password) + "\"");
						DateTime deadline=DateTime.Now.AddSeconds(20);// argon2 на стороне maddy может быть медленным
						while(DateTime.Now<deadline){
							string line=await rd.ReadLineAsync();
							if(line==null) break;
							if(line.StartsWith("A1 ")) return line.StartsWith("A1 OK");
						}
						try{ await wr.WriteLineAsync("A2 LOGOUT"); }catch{}
						return false;
					}
				}
			}
		} catch{ return false; }
	}
}

static class Sessions{
	public class S{ public string Uid; public DateTime Expires; }
	static readonly object Lk=new object();
	static Dictionary<string,S> _map=new Dictionary<string,S>();
	static string _path;
	public static void Init(string dataRoot){
		_path=Path.Combine(dataRoot,"sessions.json");
		try{ if(File.Exists(_path)) _map=JsonConvert.DeserializeObject<Dictionary<string,S>>(File.ReadAllText(_path))??new Dictionary<string,S>(); }catch{}
		Sweep();
	}
	static void Save(){ lock(Lk){ try{ File.WriteAllText(_path,JsonConvert.SerializeObject(_map,Formatting.Indented)); }catch{} } }
	static void Sweep(){ lock(Lk){ DateTime now=DateTime.Now; foreach(string k in _map.Where(kv=>kv.Value.Expires<now).Select(kv=>kv.Key).ToList()) _map.Remove(k); } }
	public static string Create(string uid,int days){
		byte[] b=new byte[32]; RandomNumberGenerator.Fill(b);
		string tok=Convert.ToBase64String(b).Replace('+','-').Replace('/','_').TrimEnd('=');
		lock(Lk){ _map[tok]=new S{Uid=uid,Expires=DateTime.Now.AddDays(days)}; }
		Save(); return tok;
	}
	public static S Resolve(string tok){
		if(string.IsNullOrEmpty(tok)) return null;
		lock(Lk){
			if(!_map.TryGetValue(tok,out S s)) return null;
			if(s.Expires<DateTime.Now){ _map.Remove(tok); return null; }
			return s;
		}
	}
	public static void Destroy(string tok){ lock(Lk){ _map.Remove(tok); } Save(); }
}

static class UserStore{
	static readonly object Lk=new object();
	static readonly ConcurrentDictionary<string,Profile> _cache=new ConcurrentDictionary<string,Profile>();
	static readonly List<string> _lru=new List<string>();
	const int CacheMax=20;
	public static string Root;
	public static AuthCfg Cfg;
	public static string DirFor(string uid)=>Path.Combine(Root,"users",uid);
	public static int UserCount(){ try{ return Directory.GetDirectories(Path.Combine(Root,"users")).Length; }catch{ return 0; } }
	// null = сервер заполнен (новый пользователь при достигнутом cap)
	public static Profile GetOrCreate(string uid){
		lock(Lk){
			if(_cache.TryGetValue(uid,out Profile p)){ TouchLru(uid); return p; }
			string dir=DirFor(uid);
			if(!Directory.Exists(dir)){
				if(UserCount()>=Cfg.MaxUsers) return null;
				Directory.CreateDirectory(dir);
			}
			p=new Profile(dir); p.Uid=uid; p.Load();
			_cache[uid]=p; TouchLru(uid);
			while(_lru.Count>CacheMax){ string ev=_lru[0]; _lru.RemoveAt(0); _cache.TryRemove(ev,out _); }
			return p;
		}
	}
	static void TouchLru(string uid){ _lru.Remove(uid); _lru.Add(uid); }
	public static IEnumerable<Profile> Loaded(){ return _cache.Values.ToList(); }
	public static Profile Peek(string uid){ lock(Lk){ return _cache.TryGetValue(uid,out Profile p)?p:null; } }// без загрузки с диска и без touch LRU
}

static class RateLimit{
	static readonly ConcurrentDictionary<string,(int Fails,DateTime Until)> _f=new ConcurrentDictionary<string,(int,DateTime)>();
	// true = можно пробовать; false = заблокирован (backoff)
	public static bool Allow(string key,int failDelaySec){
		if(!_f.TryGetValue(key,out var e)) return true;
		return e.Until<=DateTime.Now;
	}
	public static void Fail(string key,int failDelaySec){
		_f.AddOrUpdate(key,
			k=>(1,DateTime.Now.AddSeconds(failDelaySec)),
			(k,e)=>(e.Fails+1,DateTime.Now.AddSeconds(Math.Min(60,failDelaySec*Math.Pow(2,e.Fails)))));
	}
	public static void Ok(string key){ _f.TryRemove(key,out _); }
}
}
