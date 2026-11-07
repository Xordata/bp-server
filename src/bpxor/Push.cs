using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
// Фаза 4: доставка уведомлений — Web Push (VAPID + NuGet WebPush) и email-фолбэк
// через maddy submission (STARTTLS на loopback, бот-аккаунт).

// VAPID-ключи: EC P-256, генерируются один раз на сервер, лежат в <dataRoot>/push/vapid.json.
static class VapidKeys{
	public static string Subject="mailto:admin@example.org";
	static string _public,_private;
	static readonly object Lk=new object();
	public static string PublicKey{ get{ lock(Lk){ Ensure(); } return _public; } }
	public static string PrivateKey{ get{ lock(Lk){ Ensure(); } return _private; } }
	public static string B64u(byte[] b){ return Convert.ToBase64String(b).Replace('+','-').Replace('/','_').TrimEnd('='); }
	public static void Init(string dataRoot){
		string path=Path.Combine(dataRoot,"push","vapid.json");
		try{
			if(File.Exists(path)){
				JObject j=JObject.Parse(File.ReadAllText(path));
				_public=(string)j["public"];_private=(string)j["private"];
				if(!string.IsNullOrWhiteSpace((string)j["subject"]))Subject=(string)j["subject"];
			}
		}catch{}
		if(string.IsNullOrEmpty(_public)||string.IsNullOrEmpty(_private)){
			using(ECDiffieHellman ec=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)){
				ECParameters p=ec.ExportParameters(true);
				byte[] q=new byte[65];q[0]=4;
				Buffer.BlockCopy(p.Q.X,0,q,1,32);Buffer.BlockCopy(p.Q.Y,0,q,33,32);
				_public=B64u(q);_private=B64u(p.D);
			}
			try{
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path,JsonConvert.SerializeObject(new{subject=Subject, @public=_public, @private=_private},Formatting.Indented));
			try{ File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite); }catch{}// приватный ключ VAPID — 0600 (на Windows no-op)
			}catch(Exception ex){ Console.WriteLine("vapid save: "+ex.Message); }
		}
	}
	static void Ensure(){ if(string.IsNullOrEmpty(_public))Init(Program.ACfg.DataRoot); }
}

// Подписки Web Push: <dataRoot>/push/subs.json — uid -> [{endpoint,p256dh,auth}].
static class PushStore{
	public class Sub{ public string Endpoint; public string P256dh; public string Auth; public DateTime Added; }
	static readonly object Lk=new object();
	static Dictionary<string,List<Sub>> _map;
	static string _path;
	public static void Init(string dataRoot){
		_path=Path.Combine(dataRoot,"push","subs.json");
		try{ if(File.Exists(_path)) _map=JsonConvert.DeserializeObject<Dictionary<string,List<Sub>>>(File.ReadAllText(_path))??new Dictionary<string,List<Sub>>(); }catch{}
		_map=_map??new Dictionary<string,List<Sub>>();
	}
	static void Save(){ try{ File.WriteAllText(_path,JsonConvert.SerializeObject(_map,Formatting.Indented)); try{ File.SetUnixFileMode(_path,UnixFileMode.UserRead|UnixFileMode.UserWrite); }catch{} }catch{} }// auth-ключи подписок — 0600
	public static void Add(string uid,Sub s){
		lock(Lk){
			if(!_map.TryGetValue(uid,out List<Sub> l))_map[uid]=l=new List<Sub>();
			l.RemoveAll(x=>x.Endpoint==s.Endpoint);// одна подписка на endpoint
			s.Added=DateTime.Now; l.Add(s);
			Save();
		}
	}
	// true = подписка найдена и удалена
	public static bool Remove(string uid,string endpoint){
		lock(Lk){
			if(!_map.TryGetValue(uid,out List<Sub> l))return false;
			int n=l.RemoveAll(x=>x.Endpoint==endpoint);
			if(n>0)Save();
			return n>0;
		}
	}
	public static int Count(string uid){ lock(Lk){ return _map.TryGetValue(uid,out List<Sub> l)?l.Count:0; } }
	public static bool Has(string uid,string endpoint){ lock(Lk){ return _map.TryGetValue(uid,out List<Sub> l)&&l.Any(x=>x.Endpoint==endpoint); } }
	// доставка всем подпискам юзера; true = хотя бы одна доставлена.
	// 404/410 (подписка протухла) — подписку чистим.
	public static async Task<bool> SendToUser(string uid,string title,string body){
		List<Sub> subs;
		lock(Lk){ subs=_map.TryGetValue(uid,out List<Sub> l)?l.ToList():new List<Sub>(); }
		if(subs.Count==0)return false;
		string payload=JObject.FromObject(new{title,body,tag="bpxor",renotify=true,vibrate=new[]{300,150,300,150,300}}).ToString(Formatting.None);
		var push=new WebPush.WebPushClient();
		push.SetVapidDetails(VapidKeys.Subject,VapidKeys.PublicKey,VapidKeys.PrivateKey);
		// TTL 1 ч: 60 с не доживал до телефона в doze (FCM отдаёт очередь при пробуждении — «на телефон пуши не приходят»).
		// tag+renotify в payload: задержанные дубли заменяют друг друга, а не копятся (Urgency этот вариант библиотеки не принимает)
		var headers=new Dictionary<string,object>{{"TTL",3600}};
		bool any=false;
		foreach(Sub s in subs){
			try{
				await push.SendNotificationAsync(new WebPush.PushSubscription(s.Endpoint,s.P256dh,s.Auth),payload,headers);
				any=true;
			}catch(WebPush.WebPushException ex){
				if(ex.StatusCode==System.Net.HttpStatusCode.NotFound||ex.StatusCode==System.Net.HttpStatusCode.Gone)Remove(uid,s.Endpoint);
				Console.WriteLine("push fail "+uid+": "+ex.Message); Watchdog.PushFailInc(ex.Message);
			}
		}
		return any;
	}
}

// Минималистичный SMTP-клиент (submission 587, STARTTLS) — как MaddyAuth, без новых зависимостей.
// частотный ограничитель «экспорта в почту» (uid -> метки последних отправок, скользящее окно)
static class MailRate{
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string,List<DateTime>> _hits=new System.Collections.Concurrent.ConcurrentDictionary<string,List<DateTime>>();
	public static bool Allow(string uid,int max,int windowSec){
		DateTime now=DateTime.Now;
		List<DateTime> list=_hits.GetOrAdd(uid,_=>new List<DateTime>());
		lock(list){ list.RemoveAll(t=>(now-t).TotalSeconds>windowSec); if(list.Count>=max) return false; list.Add(now); return true; }
	}
}
static class SmtpSender{
	public static void Send(AuthCfg cfg,string to,string subject,string body){ Send(cfg,to,subject,body,null,null,null); }
	// attName/attCtype/att — вложение («экспорт в почту»); null = текстовое письмо
	public static void Send(AuthCfg cfg,string to,string subject,string body,string attName,string attCtype,byte[] att){
		string msg=BuildMessage(cfg,to,subject,body,attName,attCtype,att);
		string from=string.IsNullOrWhiteSpace(cfg.SmtpFrom)?cfg.SmtpUser:cfg.SmtpFrom;// From не хранится в коде — берётся из конфига
		string ehlo=from.Contains("@")?from.Substring(from.IndexOf('@')+1):"localhost";
		if(to.IndexOfAny(new char[]{(char)13,(char)10})>=0||from.IndexOfAny(new char[]{(char)13,(char)10})>=0) throw new Exception("smtp: управляющие символы в адресе");// CRLF-инъекция SMTP-команд
		using(TcpClient tcp=new TcpClient()){
			var connect=tcp.ConnectAsync(cfg.SmtpHost,cfg.SmtpPort);
			if(!connect.Wait(10000))throw new TimeoutException("smtp connect");
			Stream io=tcp.GetStream();
			Read(io);// 220
			Cmd(io,"EHLO "+ehlo);
			Cmd(io,"STARTTLS");
			var ssl=new SslStream(io,false,(s,c,ch,e)=>true);// loopback — принимаем любой сертификат (как IMAP-probe)
			ssl.AuthenticateAsClient(cfg.SmtpHost);io=ssl;
			Cmd(io,"EHLO "+ehlo);
			Cmd(io,"AUTH LOGIN "+Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.SmtpUser)));
			Cmd(io,Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.SmtpPass)));
			Cmd(io,"MAIL FROM:<"+from+">");
			Cmd(io,"RCPT TO:<"+to+">");
			Cmd(io,"DATA");
			byte[] b=Encoding.UTF8.GetBytes(msg);
			io.Write(b,0,b.Length);io.Flush();
			Read(io);// 250 queued
			try{ Cmd(io,"QUIT"); }catch{}
		}
	}
	// сборка письма отдельно от SMTP-сессии — проверяется selftest'ом без сети
	public static string BuildMessage(AuthCfg cfg,string to,string subject,string body,string attName,string attCtype,byte[] att){
		string enc(string s){ return "=?UTF-8?B?"+Convert.ToBase64String(Encoding.UTF8.GetBytes(s))+"?="; }
		string from=string.IsNullOrWhiteSpace(cfg.SmtpFrom)?cfg.SmtpUser:cfg.SmtpFrom;
		to=(to??"").Replace("\r","").Replace("\n",""); subject=(subject??"").Replace("\r","").Replace("\n",""); attName=(attName??"").Replace("\r","").Replace("\n","").Replace("\"","'");
		StringBuilder msg=new StringBuilder();
		msg.Append("From: ").Append(from).Append("\r\n");
		msg.Append("To: ").Append(to).Append("\r\n");
		msg.Append("Subject: ").Append(enc(subject)).Append("\r\n");
		msg.Append("Date: ").Append(DateTime.UtcNow.ToString("r",CultureInfo.InvariantCulture)).Append("\r\n");
		string dom=from.Contains("@")?from.Substring(from.IndexOf('@')+1):"localhost";
		msg.Append("Message-ID: <"+Guid.NewGuid().ToString("N")+"@"+dom+">\r\n");
		msg.Append("MIME-Version: 1.0\r\n");
		string text=(body??"").Replace("\r\n","\n").Replace("\n","\r\n");
		if(text.StartsWith(".")) text="."+text;// dot-stuffing RFC 5321: строка с точки раньше времени закрыла бы DATA
		text=text.Replace("\r\n.","\r\n..");
		if(att==null||att.Length==0){
			msg.Append("Content-Type: text/plain; charset=utf-8\r\n");
			msg.Append("Content-Transfer-Encoding: 8bit\r\n\r\n");
			msg.Append(text).Append("\r\n.\r\n");
		} else {
			string bd="bpxor"+Guid.NewGuid().ToString("N").Substring(0,16);
			msg.Append("Content-Type: multipart/mixed; boundary=\""+bd+"\"\r\n\r\n");
			msg.Append("--").Append(bd).Append("\r\n");
			msg.Append("Content-Type: text/plain; charset=utf-8\r\n");
			msg.Append("Content-Transfer-Encoding: 8bit\r\n\r\n");
			msg.Append(text).Append("\r\n");
			msg.Append("--").Append(bd).Append("\r\n");
			msg.Append("Content-Type: ").Append(attCtype??"application/octet-stream").Append("; filename=\"").Append(attName).Append("\"\r\n");
			msg.Append("Content-Transfer-Encoding: base64\r\n");
			msg.Append("Content-Disposition: attachment; filename=\"").Append(attName).Append("\"\r\n\r\n");
			string b64=Convert.ToBase64String(att);
			for(int i=0;i<b64.Length;i+=76) msg.Append(b64,i,Math.Min(76,b64.Length-i)).Append("\r\n");
			msg.Append("--").Append(bd).Append("--\r\n.\r\n");
		}
		return msg.ToString();
	}
	static string Read(Stream io){
		StringBuilder sb=new StringBuilder();
		int cur=io.ReadByte();
		while(cur!=-1){
			sb.Append((char)cur);
			if(cur=='\r'&&io.ReadByte()=='\n'){
				string line=sb.ToString();sb.Clear();
				if(line.Length>=4&&line[3]=='-')continue;// многострочный ответ
				return line.TrimEnd('\r','\n');
			}
			cur=io.ReadByte();
		}
		return sb.ToString();
	}
	static void Cmd(Stream io,string cmd){
		byte[] b=Encoding.UTF8.GetBytes(cmd+"\r\n");
		io.Write(b,0,b.Length);io.Flush();
		string r=Read(io);
		if(r.Length<3||!int.TryParse(r.Substring(0,3),out int code)||code>=400)throw new IOException("smtp: "+r);
	}
}
}
