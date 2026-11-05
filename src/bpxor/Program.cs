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

		var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new string[0]);
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
		if(path=="/logout"){ string t0=GetCookie(req,CookieSid); if(t0!=null) Sessions.Destroy(t0); DelCookie(ctx,CookieSid); DelCookie(ctx,CookieCsrf); ctx.Response.Redirect("/login"); return; }
		// ---- сессия ----
		var s=Sessions.Resolve(GetCookie(req,CookieSid));
		bool isApi=path.StartsWith("/api/");
		if(s==null){
			if(isApi){ ctx.Response.StatusCode=401; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"не авторизован\"}"); return; }
			if(path=="/"||path=="/index.html"||path=="/alarm.mp3"){ ctx.Response.Redirect("/login"); return; }
			await next(); return;
		}
		Profile p=UserStore.GetOrCreate(s.Uid);
		if(p==null){ ctx.Response.StatusCode=403; ctx.Response.ContentType="application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"ok\":false,\"err\":\"достигнут лимит пользователей сервера\"}"); return; }
		ctx.Items["profile"]=p; ctx.Items["uid"]=s.Uid;
		// ---- CSRF на мутающих запросах (double-submit: cookie == заголовок) ----
		if(req.Method=="POST"&&isApi){
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
				try{ p.CheckAlarms(); p.CheckMedReminders(); }catch(Exception ex){ p.Log("alarms: "+ex.Message); }
				if(tick%3600==0) p.DailyBackup();
				if((DateTime.Now-p.LastPoll).TotalMinutes<Math.Max(1,p.Cfg.PollMin)) continue;
				p.LastPoll=DateTime.Now;
				p.PollWeather();
			}
		}
	}
}
}
