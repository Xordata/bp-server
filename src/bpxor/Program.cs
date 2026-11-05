// bpxor — веб-порт BPMonitor. Фаза 1: один пользователь (--data указывает каталог профиля),
// Kestrel на 127.0.0.1 за nginx. Авторизация/мультипользовательскость — Фаза 2.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
	static int Main(string[] args){
		// -------- CLI: самотест (временный каталог, реальные данные не трогаем) --------
		if(args.Length>0&&args[0]=="--selftest"){
			string dir=Path.Combine(Path.GetTempPath(),"bpxor-selftest-"+Guid.NewGuid().ToString("N").Substring(0,8));
			Directory.CreateDirectory(dir);
			App.InitPaths(dir);
			int bad=App.SelfTest();
			try{ Directory.Delete(dir,true); }catch{}
			return bad;
		}
		// -------- каталог данных: --data <dir> (по умолчанию ./data-run рядом с бинарём) --------
		string dataDir=null;
		for(int i=0;i<args.Length-1;i++) if(args[i]=="--data") dataDir=args[i+1];
		if(string.IsNullOrEmpty(dataDir)) dataDir=Path.Combine(AppContext.BaseDirectory,"data-run");
		Directory.CreateDirectory(dataDir);
		App.InitPaths(dataDir);
		App.Load();
		App.DailyBackup();

		var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new string[0]);
		builder.Logging.ClearProviders();
		builder.Logging.AddSimpleConsole(o=>{o.SingleLine=true;o.TimestampFormat="HH:mm:ss ";});
		builder.WebHost.ConfigureKestrel(k=>{
			k.Listen(IPAddress.Loopback,App.Cfg.Port);   // только loopback: наружу смотрит nginx
			k.Limits.MaxRequestBodySize=25*1024*1024;   // импорт export/all
		});
		var app=builder.Build();
		HttpApi.Map(app);

		// фоновый цикл: будильники (30 с), погода (PollMin), бэкап (час) — семантика десктопного таймера
		_ = BackgroundLoop(app.Lifetime.ApplicationStopping);

		App.Log("bpxor старт: data="+dataDir+" port="+App.Cfg.Port);
		app.Run();
		App.Save();
		return 0;
	}

	static async Task BackgroundLoop(CancellationToken ct){
		int tick=0,lastPoll=0;
		App.PollWeather();
		while(!ct.IsCancellationRequested){
			try{ await Task.Delay(1000,ct); } catch{ break; }
			tick++;
			try{
				App.CheckAlarms();
				App.CheckMedReminders();
			}catch(Exception ex){ App.Log("alarms: "+ex.Message); }
			if(tick%3600==0) App.DailyBackup();
			if(tick-lastPoll<Math.Max(1,App.Cfg.PollMin)*60) continue;
			lastPoll=tick;
			App.PollWeather();
		}
	}
}
}
