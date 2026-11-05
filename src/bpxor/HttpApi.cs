// HTTP-слой: ASP.NET Core minimal API. Пути/JSON-семантика/блокировки — один в один с десктопным
// p.Handle(HttpListenerContext). Отличия: нет IsLocalSameOrigin (заменит сессия из Фазы 2,
// Kestrel слушает только loopback), HEAD покрывается MapMethods.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
static class HttpApi{
	static async Task<string> ReadBody(HttpRequest r){
		using(StreamReader sr=new StreamReader(r.Body,Encoding.UTF8)) return await sr.ReadToEndAsync();
	}
	static IResult Json(object o)=>Results.Content(JsonConvert.SerializeObject(o),"application/json; charset=utf-8");
	static IResult Err(string msg)=>Results.Content("{\"ok\":false,\"err\":\""+(msg??"").Replace("\"","'")+"\"}","application/json; charset=utf-8");
	static IResult File(byte[] b,string ctype,string fname=null){
		if(fname!=null) return Results.File(b,ctype+"; charset=utf-8",fname);
		return Results.Content(Encoding.UTF8.GetString(b),ctype+"; charset=utf-8");
	}

	public static void Map(WebApplication app){
		// ---------- статика ----------
		app.MapMethods("/",new[]{HttpMethods.Get,HttpMethods.Head},()=>Index());
		app.MapMethods("/index.html",new[]{HttpMethods.Get,HttpMethods.Head},()=>Index());
		app.MapGet("/alarm.mp3",()=>{
			string p=Path.Combine(AppContext.BaseDirectory,"alarm.mp3");
			return System.IO.File.Exists(p)?Results.File(p,"audio/mpeg"):Results.NotFound();
		});

		// ---------- GET ----------
		app.MapMethods("/api/state",new[]{HttpMethods.Get,HttpMethods.Head},(HttpContext ctx)=>Guard(()=>Json(P(ctx).StateJson()))());
		app.MapMethods("/api/nn/predictions",new[]{HttpMethods.Get,HttpMethods.Head},(HttpContext ctx)=>Guard(()=>{Profile pr=P(ctx);lock(pr.Sync){return Json(pr.Predictions());}})());
		app.MapMethods("/api/version",new[]{HttpMethods.Get,HttpMethods.Head},()=>Json(new{version="bpxor 0.1.0"}));
		app.MapMethods("/api/job",new[]{HttpMethods.Get,HttpMethods.Head},(HttpContext ctx)=>Json(P(ctx).JobJson()));
		app.MapMethods("/api/me",new[]{HttpMethods.Get,HttpMethods.Head},(HttpContext ctx)=>Json(new{uid=(string)ctx.Items["uid"]}));
		app.MapMethods("/api/export/all",new[]{HttpMethods.Get,HttpMethods.Head},(HttpContext ctx)=>{
			Profile pr=P(ctx);
			lock(pr.Sync){
				byte[] b=Encoding.UTF8.GetBytes(pr.ExportAll().ToString(Formatting.Indented));
				return Results.File(b,"application/json; charset=utf-8","bpmonitor-export-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json");
			}
		});

		// ---------- запуск фоновых задач (вне lock: сама задача держит Sync минутами) ----------
		app.MapPost("/api/nn/train_ga",ctx=>StartJob("train_ga",ctx));
		app.MapPost("/api/nn/validate",ctx=>StartJob("validate",ctx));
		app.MapPost("/api/nn/tune",ctx=>StartJob("tune",ctx));
		app.MapPost("/api/weather/backfill",ctx=>StartJob("backfill",ctx));

		// ---------- POST-мутации (под lock(Sync), как в десктопе) ----------
		app.MapPost("/api/measurement",ctx=>Post(ctx,(p,jo,body)=>{
			Measurement m=new Measurement{
				Id=Guid.NewGuid().ToString("N"),
				T=p.JT(jo,"t"),
				Sys=(int)jo["sys"],Dia=(int)jo["dia"],Pulse=(int)jo["pulse"],
				Note=jo["note"]!=null?jo["note"].ToString():""
			};
			if(jo["meds"] is JArray) m.Meds=jo["meds"].Values<string>().Where(x=>x!="").ToList();
			p.Snapshot(m);
			if(m.T<=DateTime.Now.AddHours(-2)) p.FillWx(m);
			p.D.M.Add(m);
			p.Train();
			p.AppendMeasurement(m);
			p.SaveNn();
			p.MaybePopup("запись");
			return Json(new{ok=true,id=m.Id});
		}));
		app.MapPost("/api/measurement/update",ctx=>Post(ctx,(p,jo,body)=>{
			Measurement m=p.D.M.FirstOrDefault(x=>x.Id==jo["id"].ToString());
			if(m!=null){
				m.T=p.JT(jo,"t");
				m.Sys=(int)jo["sys"];
				m.Dia=(int)jo["dia"];
				m.Pulse=(int)jo["pulse"];
				m.Note=jo["note"]!=null?jo["note"].ToString():"";
				if(m.T<=DateTime.Now.AddHours(-2)) p.FillWx(m);
				if(jo["meds"] is JArray) m.Meds=jo["meds"].Values<string>().Where(x=>x!="").ToList();
				p.Train();
				p.AppendMeasurement(m);
				p.SaveNn();
			}
			return Json(new{ok=true});
		}));
		app.MapPost("/api/measurement/delete",ctx=>Post(ctx,(p,jo,body)=>{
			Measurement m=p.D.M.FirstOrDefault(x=>x.Id==jo["id"].ToString());
			if(m!=null){
				p.D.M.Remove(m);
				p.Train();
				p.AppendGuidRec(Profile.RecMeasDel,m.Id);
				p.SaveNn();
			}
			return Json(new{ok=true});
		}));
		app.MapPost("/api/emotion",ctx=>Post(ctx,(p,jo,body)=>{
			EmotionEntry ee=new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=p.JT(jo,"t"),Val=(int)jo["val"],Note=jo["note"]!=null?jo["note"].ToString():""};
			p.D.E.Add(ee);
			p.AppendEmotion(ee);
			foreach(Measurement mm in p.D.M.Where(x=>ee.T>=x.T&&ee.T-x.T<=TimeSpan.FromMinutes(10)).ToList()){ p.SnapshotContext(mm); p.AppendMeasurement(mm); }
			return Json(new{ok=true});
		}));
		app.MapPost("/api/noise",ctx=>Post(ctx,(p,jo,body)=>{
			NoiseEntry ne=new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=p.JT(jo,"t"),Type=jo["type"].ToString(),Note=jo["note"]!=null?jo["note"].ToString():""};
			p.D.N.Add(ne);
			if(jo["addType"]!=null&&jo["addType"].ToString()!=""&&!p.Cfg.Noises.Contains(jo["addType"].ToString())){ p.Cfg.Noises.Add(jo["addType"].ToString()); p.SaveCfg(); }
			p.AppendNoise(ne);
			foreach(Measurement mm in p.D.M.Where(x=>ne.T>=x.T&&ne.T-x.T<=TimeSpan.FromMinutes(10)).ToList()){ p.SnapshotContext(mm); p.AppendMeasurement(mm); }
			return Json(new{ok=true});
		}));
		app.MapPost("/api/sleep",ctx=>Post(ctx,(p,jo,body)=>{
			SleepEntry se=new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=p.JT(jo,"t"),Hours=(float)jo["h"],Quality=(int)jo["q"],Note=jo["note"]!=null?jo["note"].ToString():""};
			p.D.S.Add(se);
			p.AppendSleep(se);
			foreach(Measurement mm in p.D.M.Where(x=>se.T>=x.T&&se.T-x.T<=TimeSpan.FromMinutes(10)).ToList()){ p.SnapshotContext(mm); p.AppendMeasurement(mm); }
			return Json(new{ok=true});
		}));
		app.MapPost("/api/journal/delete",ctx=>Post(ctx,(p,jo,body)=>{
			string id=jo["id"].ToString();
			if(p.D.E.Any(x=>x.Id==id)){ p.D.E.Remove(p.D.E.First(x=>x.Id==id)); p.AppendGuidRec(Profile.RecEmoDel,id); }
			else if(p.D.N.Any(x=>x.Id==id)){ p.D.N.Remove(p.D.N.First(x=>x.Id==id)); p.AppendGuidRec(Profile.RecNoiDel,id); }
			else if(p.D.S.Any(x=>x.Id==id)){ p.D.S.Remove(p.D.S.First(x=>x.Id==id)); p.AppendGuidRec(Profile.RecSleepDel,id); }
			return Json(new{ok=true});
		}));
		app.MapPost("/api/sleep/import",ctx=>Post(ctx,(p,jo,body)=>{
			System.Collections.Generic.List<SleepEntry> imported=new System.Collections.Generic.List<SleepEntry>();
			int n=p.ImportSleepCsv(jo["csv"].ToString(),out string err,imported);
			if(n>0){
				p.Train();
				foreach(SleepEntry se in imported) p.AppendSleep(se);
				p.SaveNn();
			}
			return Json(new{ok=true,added=n,err=err});
		}));
		app.MapPost("/api/config",ctx=>Post(ctx,(p,jo,body)=>{
			int oldPort=p.Cfg.Port;
			string oldToken=p.Cfg.Ha!=null?p.Cfg.Ha.Token:"";
			string oldDob=p.Cfg.Dob??"",oldSex=p.Cfg.Sex??"";
			Config nc=JsonConvert.DeserializeObject<Config>(JsonConvert.SerializeObject(p.Cfg),new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace});
			JsonConvert.PopulateObject(body,nc,new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace});// Replace: массивы из тела ЗАМЕНЯЮТ существующие
			if(nc.Ha!=null&&nc.Ha.Token==Profile.TokenMask) nc.Ha.Token=oldToken;
			nc.Meds=nc.Meds.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			nc.Noises=nc.Noises.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if(nc.MedKt!=null){ System.Collections.Generic.Dictionary<string,MedKt> mk=new System.Collections.Generic.Dictionary<string,MedKt>(); foreach(var kv in nc.MedKt) if(!string.IsNullOrWhiteSpace(kv.Key)) mk[p.NormMed(kv.Key)]=kv.Value; nc.MedKt=mk; }
			if(nc.MedTimes!=null){
				System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<string>> mt=new System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<string>>();
				foreach(var kv in nc.MedTimes){
					string k=p.NormMed(kv.Key); if(k==""||kv.Value==null) continue;
					System.Collections.Generic.List<string> ts=kv.Value.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Where(x=>System.Text.RegularExpressions.Regex.IsMatch(x,"^([01]?\\d|2[0-3]):[0-5]\\d$")).Distinct().ToList();
					if(ts.Count>0) mt[k]=ts;
				}
				nc.MedTimes=mt;
			}
			p.Cfg=nc;
			p.SaveCfg();
			p.EnsureNnDim();
			if(p.Cfg.Dob!=oldDob||p.Cfg.Sex!=oldSex){ p.Train(); p.SaveNn(); }// возраст/пол поменялись -> переучить модель на новых значениях фич
			return Json(new{ok=true,restartRequired=p.Cfg.Port!=oldPort});
		}));
		app.MapPost("/api/autostart",ctx=>Post(ctx,(p,jo,body)=>{ p.SetAutostart((bool)jo["on"]); p.SaveCfg(); return Json(new{ok=true}); }));
		app.MapPost("/api/alarm/test",ctx=>Post(ctx,(p,jo,body)=>{ p.StartAlarm(); return Json(new{ok=true}); }));
		app.MapPost("/api/alarm/stop",ctx=>Post(ctx,(p,jo,body)=>{ p.StopAlarm(); return Json(new{ok=true}); }));
		app.MapPost("/api/nn/train",ctx=>Post(ctx,(p,jo,body)=>{ p.Train(); p.SaveNn(); return Json(new{ok=true}); }));
		app.MapPost("/api/nn/reset",ctx=>Post(ctx,(p,jo,body)=>{ p.D.Nn=p.NewNn(p.Cfg.Nn.Hidden,p.Cfg.Nn.Hidden2,p.InDim()); p.Train(); p.SaveNn(); return Json(new{ok=true}); }));
		app.MapPost("/api/medtake",ctx=>Post(ctx,(p,jo,body)=>{
			string med=jo["med"]!=null?jo["med"].ToString().Trim():"";
			if(string.IsNullOrWhiteSpace(med)) return Json(new{ok=false,err="не указано лекарство"});
			DateTime t=DateTime.Now;
			if(jo["t"]!=null){ try{ t=p.JT(jo,"t"); }catch{} }
			MedTakeEntry mt=new MedTakeEntry{Id=Guid.NewGuid().ToString("N"),T=t,Med=med};
			p.D.MedTakes.Add(mt);
			p.AppendMedTake(mt);
			p.Train();
			p.SaveNn();
			return Json(new{ok=true,id=mt.Id});
		}));
		app.MapPost("/api/medtake/delete",ctx=>Post(ctx,(p,jo,body)=>{
			string id=jo["id"].ToString();
			MedTakeEntry mt=p.D.MedTakes.FirstOrDefault(x=>x.Id==id);
			if(mt!=null){
				p.D.MedTakes.Remove(mt);
				p.AppendGuidRec(Profile.RecMedTakeDel,id);
				p.Train();
				p.SaveNn();
			}
			return Json(new{ok=true});
		}));
		app.MapPost("/api/medpk",ctx=>Post(ctx,(p,jo,body)=>{
			System.Collections.Generic.Dictionary<string,double[]> nd=new System.Collections.Generic.Dictionary<string,double[]>();
			JObject e=jo["entries"] as JObject;
			if(e!=null) foreach(var kv in e){
				JArray a=kv.Value as JArray; if(a==null||a.Count<2) continue;
				string k=p.NormMed(kv.Key); if(k=="") continue;
				double h,d;
				if(double.TryParse((string)a[0],System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out h)
					&&double.TryParse((string)a[1],System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out d)&&h>0&&d>0) nd[k]=new[]{h,d};
			}
			if(nd.Count==0) return Json(new{ok=false,err="пустой справочник"});
			p.MedPkDb=nd;
			p.SaveMedPk();
			p.Train();
			p.SaveNn();
			return Json(new{ok=true,count=nd.Count});
		}));
		app.MapPost("/api/import/all",ctx=>Post(ctx,(p,jo,body)=>{
			if(jo["confirm"]==null||!(bool)jo["confirm"]) return Json(new{ok=false,err="нужен confirm:true — импорт заменит все текущие данные"});
			string summary=p.ImportAll(jo);
			return Json(new{ok=true,imported=summary});
		}));
		app.MapPost("/api/storage/compact",ctx=>Post(ctx,(p,jo,body)=>{
			long before=System.IO.File.Exists(p.DataBinPath)?new FileInfo(p.DataBinPath).Length:0;
			p.Save();
			long after=System.IO.File.Exists(p.DataBinPath)?new FileInfo(p.DataBinPath).Length:0;
			return Json(new{ok=true,before=before,after=after});
		}));
		// ВАЖНО: вне Post()/lock(Sync): PollWeatherAsync в конце вызывает MaybePopup, который сам
		// берёт Sync — sync-over-async под lock(Sync) давал бы взаимоблокировку.
		app.MapPost("/api/weather/refresh",async ctx=>{
			Profile pr=(Profile)ctx.Items["profile"];
			await pr.PollWeatherAsync();
			await Json(new{ok=true}).ExecuteAsync(ctx);
		});
		app.MapPost("/api/export",ctx=>Post(ctx,(p,jo,body)=>{
			string kind=jo["kind"]!=null?jo["kind"].ToString():"rows";
			string fmt=jo["fmt"]!=null?jo["fmt"].ToString():"csv";
			byte[] data=p.BuildExport(jo,kind,fmt,out string fname,out string ctype);
			return File(data,ctype,fname);
		}));
	}

	static IResult Index(){
		string ip=Path.Combine(AppContext.BaseDirectory,"index.html");
		if(System.IO.File.Exists(ip)){
			byte[] b=System.IO.File.ReadAllBytes(ip);
			return Results.Content(Encoding.UTF8.GetString(b),"text/html; charset=utf-8");
		}
		return Results.Content("<h3>index.html не найден рядом с бинарём</h3>","text/html; charset=utf-8");
	}

	// GET-эндпоинты через Guard: единый catch как в десктопном Handle
	static Func<IResult> Guard(Func<IResult> f)=>()=>{
		try{ return f(); } catch(Exception ex){ return Err(ex.Message); }
	};

	// POST под lock(profile.Sync) — семантика десктопного Handle
	// ВАЖНО: возвращаем Task (не Task<IResult>): лямбда ctx=>... иначе биндится перегрузкой
	// RequestDelegate и возвращённый IResult молча игнорируется (200 + пустое тело).
	static Profile P(HttpContext ctx)=>(Profile)ctx.Items["profile"];
	static async Task Post(HttpContext ctx,Func<Profile,JObject,string,IResult> f){
		IResult r;
		try{
			string body=await ReadBody(ctx.Request);
			try{
				JObject jo=body.Length>0?JObject.Parse(body):new JObject();
				Profile p=P(ctx);
				lock(p.Sync){ r=f(p,jo,body); }
			} catch(Exception ex){ r=Err(ex.Message); }
		} catch(Exception ex){ r=Err(ex.Message); }
		await r.ExecuteAsync(ctx);
	}

	// фоновые задачи: ответ сразу, прогресс в /api/job (десктопная семантика)
	static async Task StartJob(string kind,HttpContext ctx){
		Profile p=P(ctx);
		string jb;
		try{ jb=await ReadBody(ctx.Request); } catch(Exception ex){ await Err(ex.Message).ExecuteAsync(ctx); return; }
		JObject jo=null; try{ jo=jb.Length>0?JObject.Parse(jb):new JObject(); }catch{}
		if(!p.TryStartJob(kind)){ await Json(new{ok=false,err="уже выполняется: "+p.CurrentJobName}).ExecuteAsync(ctx); return; }
		DateTime? fu=null;
		if(kind=="backfill"&&jo!=null&&jo["force_until"]!=null){ try{ fu=p.JT(jo,"force_until"); }catch{} }
		int holdout=0;
		if((kind=="validate"||kind=="tune")&&jo!=null&&jo["holdout"]!=null){ int.TryParse((string)jo["holdout"],out holdout); }
		bool startedNow=JobGate.Run(()=>{
			try{
				p.JobQueued=false;
				lock(p.Sync){
					switch(kind){
						case"train_ga":{ double loss=p.TrainGenetic(); p.JobResult="MSE="+Math.Round(loss,4); p.SaveNn(); break; }
						case"validate":{
							JObject r=p.Validate(holdout);
							p.SetValidation(r);
							p.JobResult="MAE сис="+((double)r["maeSys"]).ToString(System.Globalization.CultureInfo.InvariantCulture)+" диаст="+((double)r["maeDia"]).ToString(System.Globalization.CultureInfo.InvariantCulture)+" мм рт.ст.";
							break;
						}
						case"tune":{
							JObject r=p.Tune(holdout);
							p.SetTune(r);
							p.JobResult="слои "+(int)r["hidden"]+"/"+(int)r["hidden2"]+", holdout MSE "+((double)r["holdMse"]).ToString(System.Globalization.CultureInfo.InvariantCulture)+(r["changed"]!=null&&(bool)r["changed"]?" — применено":" — оставлены текущие");
							break;
						}
						default:{ int nwx=p.BackfillWeather(fu); if(nwx>0){ p.Train(); p.SaveNn(); } p.JobResult="заполнено записей: "+nwx; break; }
					}
				}
			} catch(Exception ex){ p.JobError=ex.Message; p.Log(kind+": "+ex.Message); }
			finally{ p.FinishJob(); }
		},()=>p.JobQueued=true);
		await Json(new{ok=true,started=true,queued=!startedNow}).ExecuteAsync(ctx);
	}
}
}
