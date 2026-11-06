// bpxor — веб-порт BPMonitor. Фаза 2: IMAP-авторизация maddy, сессии, мультипользовательскость.
// Kestrel на 127.0.0.1 за nginx; --data <root> = корень данных (users/<uid>/, sessions.json).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
static class Program{
	public static AuthCfg ACfg=new AuthCfg();

	static int Main(string[] args){
		if(args.Length>0&&args[0]=="--selftest"){
			string dir=Path.Combine(Path.GetTempPath(),"bpxor-selftest-"+Guid.NewGuid().ToString("N").Substring(0,8));
			Directory.CreateDirectory(dir);
			int bad=new Profile(dir).SelfTest();
			try{ Directory.Delete(dir,true); }catch{}
			return bad;
		}
		string dataRoot=null;
		for(int i=0;i<args.Length-1;i++) if(args[i]=="--data") dataRoot=args[i+1];
		if(string.IsNullOrEmpty(dataRoot)) dataRoot=Path.Combine(AppContext.BaseDirectory,"data-run");
		Directory.CreateDirectory(dataRoot);
		Directory.CreateDirectory(Path.Combine(dataRoot,"users"));

		string cfgPath=Path.Combine(AppContext.BaseDirectory,"bpxor.config.json");
		if(File.Exists(cfgPath)){ try{ JsonConvert.PopulateObject(File.ReadAllText(cfgPath),ACfg); }catch(Exception ex){ Console.WriteLine("config: "+ex.Message); } }
		UserStore.Root=dataRoot; UserStore.Cfg=ACfg; Sessions.Init(dataRoot);
		ACfg.DataRoot=dataRoot; VapidKeys.Init(dataRoot); PushStore.Init(dataRoot); AlarmScheduler.DataRoot=dataRoot; ApiTokens.Init(dataRoot);
		JobGate.Max=Math.Max(1,ACfg.MaxConcurrentJobs);

		var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new string[0]);
		// SSE (/api/events) держит запросы открытыми — дефолтный ShutdownTimeout 30 с = «рестарт висит полминуты»;
		// принудительно обрываем соединения через 3 с (данные пишутся синхронно, терять нечего)
		builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o=>o.ShutdownTimeout=TimeSpan.FromSeconds(3));
		builder.Logging.ClearProviders();
		builder.Logging.AddSimpleConsole(o=>{o.SingleLine=true;o.TimestampFormat="HH:mm:ss ";});
		builder.WebHost.ConfigureKestrel(k=>{
			k.Listen(IPAddress.Loopback,ACfg.Port);
			k.Limits.MaxRequestBodySize=25*1024*1024;
		});
		var app=builder.Build();
		app.Use(AuthGate);
		HttpApi.Map(app);
		_=BackgroundLoop(app.Lifetime.ApplicationStopping);
		_=AlarmScheduler.Run(app.Lifetime.ApplicationStopping);// Фаза 4: будильники по всем юзерам (не только загруженным в LRU)
		app.Run();
		return 0;
	}

	const string CookieSid="bpxsid",CookieCsrf="bpxcsrf";
	static string RandTok(){ byte[] b=new byte[24]; RandomNumberGenerator.Fill(b); return Convert.ToBase64String(b).Replace('+','-').Replace('/','_').TrimEnd('='); }

	static async Task AuthGate(HttpContext ctx,Func<Task> next){
		var req=ctx.Request;
		string path=req.Path.Value??"/";
		// ---- публичные маршруты ----
		if((path=="/login"||path=="/login.html")&&req.Method=="GET"){ await ServeLoginPage(ctx); return; }
		if(path=="/api/login"&&req.Method=="POST"){ await DoLogin(ctx); return; }
		if(path=="/sw.js"&&req.Method=="GET"){ await ServeStatic(ctx,"sw.js","text/javascript; charset=utf-8"); return; }
		// favicon и иконки — публичны (нужны на странице входа); отдаются как есть, PNG браузер определит по содержимому
		if(req.Method=="GET"&&path=="/favicon.ico"){ await ServeStaticBin(ctx,"icons/icon-192.png","image/png"); return; }
		if(req.Method=="GET"&&path.StartsWith("/icons/")&&System.Text.RegularExpressions.Regex.IsMatch(path,"^/icons/[a-z0-9-]+[.]png$")){ await ServeStaticBin(ctx,path.Substring(1),"image/png"); return; }
		// EULA — публично (тексты соглашения, данных нет)
		if(req.Method=="GET"&&path=="/eula"){
			string fe=Path.Combine(ACfg.DataRoot??"","lang","eula.json");
			if(!System.IO.File.Exists(fe)){ ctx.Response.StatusCode=404; return; }
			ctx.Response.ContentType="application/json; charset=utf-8";
			ctx.Response.Headers["Cache-Control"]="no-cache";
			await ctx.Response.Body.WriteAsync(await File.ReadAllBytesAsync(fe)); return;
		}
		// языковые пакеты — публичны (пользовательских данных нет; нужны и для окна выбора после логина)
		if(req.Method=="GET"&&System.Text.RegularExpressions.Regex.IsMatch(path,"^/lang/[a-z]{2}[.]json$")){
			string f=Path.Combine(ACfg.DataRoot??"",path.Substring(1));
			if(!System.IO.File.Exists(f)){ ctx.Response.StatusCode=404; return; }
			ctx.Response.ContentType="application/json; charset=utf-8";
			ctx.Response.Headers["Cache-Control"]="public, max-age=300";
			await ctx.Response.Body.WriteAsync(await File.ReadAllBytesAsync(f)); return;
		}
		if(path=="/logout"){ string t0=GetCookie(req,CookieSid); if(t0!=null) Sessions.Destroy(t0); DelCookie(ctx,CookieSid); DelCookie(ctx,CookieCsrf); ctx.Response.Redirect("/login"); return; }
		// ---- персональный API-токен (синхронизация десктопа, Фаза 7) ----
		if(path.StartsWith("/api/sync/")){
			string auth=(string)req.Headers["Authorization"]??"";
			string tok=auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?auth.Substring(7).Trim():"";
			string ip=ctx.Connection.RemoteIpAddress?.ToString()??"?";
			// бэкофф по (ip, id-токена): перебор одного токена не блокирует легальные токены того же ip
			string rl="tok|"+ip+"|"+(tok.Contains('.')?tok.Substring(0,tok.IndexOf('.')):tok);
			if(!RateLimit.Allow(rl,ACfg.FailDelaySec)){ ctx.Response.StatusCode=429; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"слишком много попыток\"}"); return; }
			string uid=ApiTokens.Resolve(tok);
			if(uid==null){ RateLimit.Fail(rl,ACfg.FailDelaySec); ctx.Response.StatusCode=401; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"неверный токен\"}"); return; }
			RateLimit.Ok(rl);
			Profile pt=UserStore.GetOrCreate(uid);
			if(pt==null){ ctx.Response.StatusCode=403; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"достигнут лимит пользователей сервера\"}"); return; }
			ctx.Items["profile"]=pt; ctx.Items["uid"]=uid; ctx.Items["tokenauth"]=true;// CSRF не нужен: токен в заголовке, cookie не участвует
			await next(); return;
		}
		// ---- сессия ----
		var s=Sessions.Resolve(GetCookie(req,CookieSid));
		bool isApi=path.StartsWith("/api/");
		if(s==null){
			if(isApi){ ctx.Response.StatusCode=401; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"не авторизован\"}"); return; }
			if(path=="/"||path=="/index.html"||path=="/alarm.mp3"||path=="/q"){ ctx.Response.Redirect("/login"); return; }
			await next(); return;
		}
		Profile p=UserStore.GetOrCreate(s.Uid);
		if(p==null){ ctx.Response.StatusCode=403; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"достигнут лимит пользователей сервера\"}"); return; }
		ctx.Items["profile"]=p; ctx.Items["uid"]=s.Uid;
		// ---- CSRF на мутающих запросах (double-submit: cookie == заголовок) ----
		if(req.Method=="POST"&&isApi&&path!="/api/alarm/dismiss"&&ctx.Items["tokenauth"]==null){
			string ck=GetCookie(req,CookieCsrf),hd=req.Headers["X-CSRF"];
			if(string.IsNullOrEmpty(ck)||hd!=ck){ ctx.Response.StatusCode=403; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"csrf\"}"); return; }
		}
		await next();
	}

	static async Task DoLogin(HttpContext ctx){
		var req=ctx.Request;
		string body; using(StreamReader sr=new StreamReader(req.Body,Encoding.UTF8)) body=await sr.ReadToEndAsync();
		string login="",pass="";
		try{ JObject jo=JObject.Parse(body); login=((string)jo["login"]??"").Trim().ToLowerInvariant(); pass=(string)jo["pass"]??""; }catch{}
		async Task R(int code,string json){ ctx.Response.StatusCode=code; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync(json); }
		if(!login.EndsWith("@"+ACfg.Domain)||login.Length==ACfg.Domain.Length+1){ await R(401,"{\"ok\":false,\"err\":\"неверный логин или пароль\"}"); return; }
		string ip=ctx.Connection.RemoteIpAddress?.ToString()??"?";
		string key=ip+"|"+login;
		if(!RateLimit.Allow(key,ACfg.FailDelaySec)){ await R(429,"{\"ok\":false,\"err\":\"слишком много попыток, подождите\"}"); return; }
		bool ok=await MaddyAuth.Verify(ACfg.AuthHost,ACfg.AuthPort,login,pass);
		if(!ok){ RateLimit.Fail(key,ACfg.FailDelaySec); await R(401,"{\"ok\":false,\"err\":\"неверный логин или пароль\"}"); return; }
		RateLimit.Ok(key);
		if(UserStore.GetOrCreate(login)==null){ await R(403,"{\"ok\":false,\"err\":\"достигнут лимит пользователей сервера\"}"); return; }
		string tok=Sessions.Create(login,ACfg.SessionDays);
		SetCookie(ctx,CookieSid,tok,true,ACfg.SessionDays*86400);
		SetCookie(ctx,CookieCsrf,RandTok(),false,ACfg.SessionDays*86400);
		await R(200,"{\"ok\":true}");
	}

	static async Task ServeStaticBin(HttpContext ctx,string fname,string ctype){
		string f=Path.Combine(AppContext.BaseDirectory,fname);
		if(!File.Exists(f)){ ctx.Response.StatusCode=404; return; }
		ctx.Response.ContentType=ctype;
		ctx.Response.Headers["Cache-Control"]="public, max-age=86400";
		await ctx.Response.Body.WriteAsync(await File.ReadAllBytesAsync(f));
	}
	static async Task ServeStatic(HttpContext ctx,string fname,string ctype){
		string f=Path.Combine(AppContext.BaseDirectory,fname);
		if(!File.Exists(f)){ ctx.Response.StatusCode=404; return; }
		ctx.Response.ContentType=ctype;
		await ctx.Response.WriteAsync(await File.ReadAllTextAsync(f));
	}
	static async Task ServeLoginPage(HttpContext ctx){
		string f=Path.Combine(AppContext.BaseDirectory,"login.html");
		if(!File.Exists(f)){ ctx.Response.StatusCode=500; await ctx.Response.WriteAsync("login.html не найден"); return; }
		ctx.Response.ContentType="text/html; charset=utf-8";
		await ctx.Response.WriteAsync(await File.ReadAllTextAsync(f));
	}

	static string GetCookie(HttpRequest req,string name){
		if(!req.Cookies.TryGetValue(name,out string v)) return null;
		return v;
	}
	static void SetCookie(HttpContext ctx,string name,string val,bool httpOnly,int maxAge){
		ctx.Response.Cookies.Append(name,val,new CookieOptions{HttpOnly=httpOnly,Secure=ACfg.SecureCookies,SameSite=SameSiteMode.Lax,Path="/",MaxAge=TimeSpan.FromSeconds(maxAge)});
	}
	static void DelCookie(HttpContext ctx,string name){ ctx.Response.Cookies.Delete(name,new CookieOptions{Path="/",Secure=ACfg.SecureCookies}); }

	static async Task BackgroundLoop(CancellationToken ct){
		int tick=0;
		while(!ct.IsCancellationRequested){
			try{ await Task.Delay(1000,ct); } catch{ break; }
			tick++;
			foreach(Profile p in UserStore.Loaded()){
				if(tick%3600==0) p.DailyBackup();
				if((DateTime.Now-p.LastPoll).TotalMinutes<Math.Max(1,p.Cfg.PollMin)) continue;
				p.LastPoll=DateTime.Now;
				p.PollWeather();
			}
		}
	}
}
}
