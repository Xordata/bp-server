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
// Погода: open-meteo, GFZ Kp, NOAA прогноз, архив, popup (порт BpMonitor.cs)
partial class Profile
{
	// ----------------------------- погода -----------------------------
	string WmoText(int c){
		switch(c){
			case 0:return"ясно";
			case 1:return"малооблачно";
			case 2:return"переменная облачность";
			case 3:return"пасмурно";
			case 4:return"облачно";
			case 45:
			case 48:return"туман";
		}
		if(c>=51&&c<=57) return"морось";
		if(c>=61&&c<=67) return"дождь";
		if(c>=71&&c<=77) return"снег";
		if(c>=80&&c<=82) return"дождевые шквалы";
		if(c==85||c==86) return"снежные шквалы";
		return c>=95?"гроза":"неизвестно";
	}
	public async Task PollWeatherAsync(){
		if(_weatherBusy) return;
		_weatherBusy=true;
		try{
			string url;
			url=string.Format(CultureInfo.InvariantCulture,"https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,relative_humidity_2m,pressure_msl,weather_code&hourly=pressure_msl,temperature_2m,relative_humidity_2m,weather_code&timezone=auto&forecast_days=2",
			Cfg.Lat,Cfg.Lon);
			string wk=WxCache.Key(Cfg.Lat,Cfg.Lon);// live-погода и hourly-прогноз — общие на округлённые координаты
			JObject j=null;
			if(Program.ACfg.WxSource=="metno"){
				// met.no locationforecast: без ключа, UTC-время (конвертим в tz юзера), UA с контактом обязателен по политике
				string mu=string.Format(CultureInfo.InvariantCulture,"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={0:0.0000}&lon={1:0.0000}",Cfg.Lat,Cfg.Lon);
				if(!WxCache.Try("liveMetno:"+wk,WxCache.LiveTtlMin,out j)){
					using(var rq=new HttpRequestMessage(HttpMethod.Get,mu)){
						rq.Headers.UserAgent.ParseAdd("bpxor/1.0 (https://bp.xor.world; contact: "+Program.ACfg.WxContact+")");
						using(var rp=await Http.SendAsync(rq)){ rp.EnsureSuccessStatusCode(); j=JObject.Parse(await rp.Content.ReadAsStringAsync()); }
					}
					WxCache.Put("liveMetno:"+wk,j);
				}
				ApplyMetNo(j);
				j=null;// ниже open-meteo-блок не выполняется
			}
			if(j!=null||Program.ACfg.WxSource!="metno"){
			if(j==null){
				if(!WxCache.Try("live:"+wk,WxCache.LiveTtlMin,out j)){
					j=JObject.Parse(await Http.GetStringAsync(url));
					WxCache.Put("live:"+wk,j);
				}
			}
			Weather.PressMm=(float)((double)j["current"]["pressure_msl"]*0.750062);
			Weather.TempC=(float)(double)j["current"]["temperature_2m"];
			Weather.Hum=(float)(double)j["current"]["relative_humidity_2m"];
			Weather.Text=WmoText((int)(double)j["current"]["weather_code"]);
			try{// почасовой прогноз (ближайшие ~2 суток) для упреждающего popup
				JObject fh=j["hourly"] as JObject;
				JArray ft=fh!=null?fh["time"] as JArray:null;
				if(ft!=null){
					JArray fp=fh["pressure_msl"] as JArray,ftm=fh["temperature_2m"] as JArray,fhm=fh["relative_humidity_2m"] as JArray,fc=fh["weather_code"] as JArray;
					List<WxHour> fl=new List<WxHour>();
					for(int i2=0;i2<ft.Count;i2++){
						DateTime ht;
						if(!DateTime.TryParse((string)ft[i2],CultureInfo.InvariantCulture,DateTimeStyles.None,out ht)) continue;
						if(ht<DateTime.Now.AddHours(-1)) continue;
						fl.Add(new WxHour{
							T=ht,
							Press=fp!=null&&i2<fp.Count?(double?)fp[i2]:null,
							Temp=ftm!=null&&i2<ftm.Count?(double?)ftm[i2]:null,
							Hum=fhm!=null&&i2<fhm.Count?(double?)fhm[i2]:null,
							Code=fc!=null&&i2<fc.Count?(int?)(double?)fc[i2]:null
						});
					}
					if(fl.Count>0){ Forecast=fl; ForecastUpdated=DateTime.Now; }
				}
			} catch(Exception ex){ Log("Forecast: "+ex.Message); }
			}
			try{
				if(!Http.DefaultRequestHeaders.Contains("User-Agent"))
					Http.DefaultRequestHeaders.Add("User-Agent",
					"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0");
				if(!Http.DefaultRequestHeaders.Contains("Accept")) Http.DefaultRequestHeaders.Add("Accept","application/json");
				// необязательно, но полезно против WAF/бот-защиты
				if(!Http.DefaultRequestHeaders.Contains("Accept-Language")) Http.DefaultRequestHeaders.Add("Accept-Language","en-US,en;q=0.9,de;q=0.8");
				DateTime now=DateTime.UtcNow;
				DateTime start=now.AddHours(-24);// Kp — 3-часовой, сутки даёт 8 значений, хватает
				url=string.Format("https://kp.gfz.de/app/json/?start={0:yyyy-MM-ddTHH:mm:ssZ}&end={1:yyyy-MM-ddTHH:mm:ssZ}&index=Kp",
				start,now);
				JToken token;// Kp не зависит от координат — общий кэш на сервер
				if(!WxCache.Try("kp:live",WxCache.KpTtlMin,out token)){
					string jsonString=await Http.GetStringAsync(url);
					if(string.IsNullOrWhiteSpace(jsonString)) return;
					token=JToken.Parse(jsonString);
					WxCache.Put("kp:live",token);
				}
				JToken kpArr=token["Kp"];
				JToken dtArr=token["datetime"];
				if(kpArr is JArray karr&&dtArr is JArray darr){
					double currentKp=3.3;// сентинел-дефолт (Kp 3.3)
					DateTime latestTime=DateTime.MinValue;
					int n=Math.Min(karr.Count,darr.Count);
					for(int i=0;i<n;i++){
						DateTime t;
						if(!DateTime.TryParse((string)darr[i],CultureInfo.InvariantCulture,
							DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,
							out t)||t<=latestTime) continue;
						latestTime=t;
						double.TryParse((string)karr[i],NumberStyles.Number,
						CultureInfo.InvariantCulture,out currentKp);
					}
					Weather.Kp=(int)Math.Round(currentKp*10);// десятые доли
				}
			} catch(Exception ex){ Log("Kp: "+ex.Message); }
			try{// NOAA SWPC: 3-дневный прогноз Kp (3-ч бины, UTC; observed = estimated/predicted)
				List<KeyValuePair<DateTime,double>> l;
				if(!WxCache.Try("kpfc",WxCache.KpFcTtlMin,out l)){
				JArray kf=JArray.Parse(await Http.GetStringAsync("https://services.swpc.noaa.gov/products/noaa-planetary-k-index-forecast.json"));
				l=new List<KeyValuePair<DateTime,double>>();
				foreach(JToken x in kf){
					if((string)x["observed"]=="observed") continue;
					DateTime tt; double kv;
					if(DateTime.TryParse((string)x["time_tag"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out tt)
						&&double.TryParse((string)x["kp"],NumberStyles.Number,CultureInfo.InvariantCulture,out kv))
						l.Add(new KeyValuePair<DateTime,double>(tt,kv));
				}
				if(l.Count>0) WxCache.Put("kpfc",l);
				}
				if(l.Count>0) KpForecast=l;
			} catch(Exception ex){ Log("KpFc: "+ex.Message); }

			// HomeAssistant (опционально): перекрывает указанные датчики
			if(!string.IsNullOrWhiteSpace(Cfg.Ha.Url)&&!string.IsNullOrWhiteSpace(Cfg.Ha.Token)&&HaUrlSafe(Cfg.Ha.Url))
				foreach(KeyValuePair<string,string> kv in Cfg.Ha.Sensors){
					if(string.IsNullOrWhiteSpace(kv.Value)) continue;
					try{
						HttpRequestMessage req=new HttpRequestMessage(HttpMethod.Get,Cfg.Ha.Url.TrimEnd('/')+"/api/sensor/"+Uri.EscapeDataString(kv.Value)+"/state");
						req.Headers.Add("Authorization","Bearer "+Cfg.Ha.Token);
						HttpResponseMessage resp=await HaHttp.SendAsync(req);
						JObject hj=JObject.Parse(await resp.Content.ReadAsStringAsync());
						double v=(double)hj["state"];
						switch(kv.Key){
							case"pressure":Weather.PressMm=(float)(v<1000?v*0.750062:v); break;
							case"temp":Weather.TempC=(float)v; break;
							case"humidity":Weather.Hum=(float)v; break;
							case"kp":Weather.Kp=(int)Math.Round(v*10); break;// к десятых долям
						}
					} catch{}
				}
			Weather.Updated=DateTime.Now;
			MaybePopup("погода");
			MaybePopupForecast();
			PublishChange();// погода обновилась — вкладки обновят шапку через дельту
		} catch(Exception ex){ Log("PollWeather: "+ex.Message); } finally{ _weatherBusy=false; }
	}
	// SSRF-защита: Ha.Url задаёт пользователь — сервер ходит ТОЛЬКО на публичные http/https без редиректов
	static readonly System.Net.Http.HttpClient HaHttp=new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
	static bool HaUrlSafe(string u){
		Uri uu;
		if(!Uri.TryCreate(u,UriKind.Absolute,out uu)) return false;
		if(uu.Scheme!="http"&&uu.Scheme!="https") return false;
		try{
			foreach(System.Net.IPAddress ip in System.Net.Dns.GetHostAddresses(uu.Host)){
				byte[] o=ip.GetAddressBytes();
				if(System.Net.IPAddress.IsLoopback(ip)||ip.IsIPv6LinkLocal||(o.Length==4&&(o[0]==10||(o[0]==172&&o[1]>=16&&o[1]<=31)||(o[0]==192&&o[1]==168))||o.Length==16&&(o[0]&0xFE)==0xFC)||ip.IsIPv6SiteLocal||(o.Length==4&&o[0]==169&&o[1]==254)) return false;// loopback/link-local (метаданные облака)/RFC1918
			}
		} catch{ return false; }
		return true;
	}
	// symbol_code met.no -> ближайший код WMO (дальше работает общий WmoText)
	public static int? MetNoWmo(string sym){
		if(string.IsNullOrEmpty(sym))return null;
		string s=sym.ToLowerInvariant().Replace("_day","").Replace("_night","");
		switch(s){
			case"clearsky":return 0;
			case"fair":return 1;
			case"partlycloudy":return 2;
			case"cloudy":return 4;
			case"fog":return 45;
			case"lightrain":return 80;
			case"rain":return 61;
			case"heavyrain":return 63;
			case"lightrainshowers":case"rainshowers":return 80;
			case"heavyrainshowers":return 82;
			case"lightrainandthunder":case"rainandthunder":case"heavyrainandthunder":case"thunder":case"thunderstorm":return 95;
			case"lightsleet":case"sleet":case"heavysleet":return 66;
			case"lightsnow":return 71;
			case"snow":return 73;
			case"heavysnow":return 75;
			case"lightsnowshowers":case"snowshowers":return 85;
			case"heavysnowshowers":return 86;
			default:return null;
		}
	}
	// разбор met.no compact в Weather.* и Forecast (времена мет.службы — UTC, переводим в таймзону юзера)
	public void ApplyMetNo(JObject m){
		JArray ts=m["properties"]?["timeseries"] as JArray;
		if(ts==null||ts.Count==0) throw new Exception("met.no: нет timeseries");
		JObject first=ts[0] as JObject;
		JObject inst=first?["data"]?["instant"]?["details"] as JObject;
		if(inst==null) throw new Exception("met.no: нет instant");
		Weather.PressMm=(float)((double)inst["air_pressure_at_sea_level"]*0.750062);
		Weather.TempC=(float)(double)inst["air_temperature"];
		Weather.Hum=(float)(double)inst["relative_humidity"];
		string sym=(string)first["data"]?["now"]?["summary"]?["symbol_code"]
			??(string)first["data"]?["next_1_hours"]?["summary"]?["symbol_code"]??"";
		int? wc=MetNoWmo(sym); Weather.Text=WmoText(wc??-1);
		List<WxHour> fl=new List<WxHour>();
		foreach(JToken e in ts){
			JObject d=e["data"] as JObject; if(d==null) continue;
			JObject n1=d["next_1_hours"] as JObject; if(n1==null) continue;// next_6_hours (дальние сутки) для 2-суточного окна не нужны
			DateTime utc;
			if(!DateTime.TryParse((string)e["time"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out utc)) continue;
			JObject det=n1["details"] as JObject;
			fl.Add(new WxHour{
				T=AlarmScheduler.LocalNow(Cfg.Tz,utc),
				Press=det!=null?(double?)det["air_pressure_at_sea_level"]:null,
				Temp=det!=null?(double?)det["air_temperature"]:null,
				Hum=det!=null?(double?)det["relative_humidity"]:null,
				Code=MetNoWmo((string)n1["summary"]?["symbol_code"]??"")
			});
			if(fl.Count>=48) break;
		}
		if(fl.Count>0){ Forecast=fl; ForecastUpdated=DateTime.Now; }
	}
	// fire-and-forget для таймера/Snapshot: все исключения уже логируются внутри PollWeatherAsync
	public void PollWeather(){ _=PollWeatherAsync(); }

	// ----------------------------- историческая погода (архив) -----------------------------
	void EnsureBrowserHeaders(){
		if(!Http.DefaultRequestHeaders.Contains("User-Agent"))
			Http.DefaultRequestHeaders.Add("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0");
		if(!Http.DefaultRequestHeaders.Contains("Accept")) Http.DefaultRequestHeaders.Add("Accept","application/json");
		if(!Http.DefaultRequestHeaders.Contains("Accept-Language")) Http.DefaultRequestHeaders.Add("Accept-Language","en-US,en;q=0.9,de;q=0.8");
	}
	public class WxDay{
		public DateTime[] Times;
		public double?[] Pres,Temp,Hum,Code;
		public List<KeyValuePair<DateTime,double>> Kp=new List<KeyValuePair<DateTime,double>>();// старт 3-ч.bin (UTC), значение
	}
	double?[] PickHourly(JObject h,string key,int n){
		double?[] r=new double?[n];
		JArray a=h[key] as JArray;
		if(a!=null) for(int i=0;i<n&&i<a.Count;i++) r[i]=(double?)a[i];
		return r;
	}
	WxDay FetchWxDay(DateTime date){
		try{ return FetchWxDayAsync(date).GetAwaiter().GetResult(); } catch{ return null; }
	}
	async Task<WxDay> FetchWxDayAsync(DateTime date){
		EnsureBrowserHeaders();
		int daysBack=(int)(DateTime.Today-date.Date).TotalDays;
		string dk="day:"+WxCache.Key(Cfg.Lat,Cfg.Lon)+":"+date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);// архив неизменен: старые сутки — бессрочно, свежие (ERA5 догоняет) — 6 ч
		WxDay cachedDay;
		if(WxCache.Try(dk,daysBack<=5?WxCache.RecentDayTtlMin:-1,out cachedDay)) return cachedDay;
		string url;
    		if(daysBack<=5)// свежий период: forecast API (архив ERA5 отстаёт на ~5 суток)
			url=string.Format(CultureInfo.InvariantCulture,"https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&hourly=pressure_msl,temperature_2m,relative_humidity_2m,weather_code&timezone=auto&past_days={2}&forecast_days=1",Cfg.Lat,Cfg.Lon,Math.Min(91,Math.Max(0,daysBack+1)));
		else// глубже — архив ERA5: полный исторический ряд без ограничений past_days
			url=string.Format(CultureInfo.InvariantCulture,"https://archive-api.open-meteo.com/v1/archive?latitude={0}&longitude={1}&start_date={2:yyyy-MM-dd}&end_date={3:yyyy-MM-dd}&hourly=pressure_msl,temperature_2m,relative_humidity_2m,weather_code&timezone=auto",Cfg.Lat,Cfg.Lon,date.Date,date.Date);
		JObject j=JObject.Parse(await Http.GetStringAsync(url));
		JObject h=j["hourly"] as JObject; if(h==null) return null;
		JArray tt=h["time"] as JArray; if(tt==null||tt.Count==0) return null;
		WxDay d=new WxDay();
		int n=tt.Count;
		d.Times=tt.Select(x=>DateTime.Parse((string)x,CultureInfo.InvariantCulture,DateTimeStyles.None)).ToArray();
		d.Pres=PickHourly(h,"pressure_msl",n); d.Temp=PickHourly(h,"temperature_2m",n);
		d.Hum=PickHourly(h,"relative_humidity_2m",n); d.Code=PickHourly(h,"weather_code",n);
		try{// Kp за сутки: 3-часовые бины GFZ
			string kurl=string.Format(CultureInfo.InvariantCulture,"https://kp.gfz.de/app/json/?start={0:yyyy-MM-ddTHH:mm:ssZ}&end={1:yyyy-MM-ddTHH:mm:ssZ}&index=Kp",date.Date.AddDays(-1),date.Date.AddDays(2));
			JObject kj=JObject.Parse(await Http.GetStringAsync(kurl));
			JArray ka=kj["Kp"] as JArray,kd=kj["datetime"] as JArray;
			if(ka!=null&&kd!=null){
				int n2=Math.Min(ka.Count,kd.Count);
				for(int q=0;q<n2;q++){
					DateTime bt; double kv;
					if(DateTime.TryParse((string)kd[q],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out bt)
						&&double.TryParse((string)ka[q],NumberStyles.Number,CultureInfo.InvariantCulture,out kv))
						d.Kp.Add(new KeyValuePair<DateTime,double>(bt,kv));
				}
			}
		}catch{}
		if(d!=null) WxCache.Put(dk,d);
		return d;
	}
	// дозаполняет пустые поля погоды/Kp записи по архиву за m.T; true = что-то заполнено
	public bool FillWx(Measurement m,WxDay d=null){
		if(m.PressMm!=null&&m.TempC!=null&&m.Hum!=null&&m.Kp!=null) return false;
		if(d==null){ d=FetchWxDay(m.T.Date); if(d==null) return false; }
		int idx=-1; double best=double.MaxValue;
		for(int i=0;i<d.Times.Length;i++){ double dd=Math.Abs((d.Times[i]-m.T).TotalMinutes); if(dd<best){best=dd;idx=i;} }
		if(idx<0) return false;
		bool any=false;
		if(m.PressMm==null&&d.Pres[idx]!=null){ m.PressMm=(float)(d.Pres[idx].Value*0.750062); any=true; }
		if(m.TempC==null&&d.Temp[idx]!=null){ m.TempC=(float)d.Temp[idx].Value; any=true; }
		if(m.Hum==null&&d.Hum[idx]!=null){ m.Hum=(float)d.Hum[idx].Value; any=true; }
		if(string.IsNullOrEmpty(m.Wx)&&d.Code[idx]!=null){ m.Wx=WmoText((int)d.Code[idx].Value); any=true; }
		if(m.Kp==null){
			DateTime u=m.T.ToUniversalTime(); double? kv=null;
			foreach(KeyValuePair<DateTime,double> p in d.Kp) if(p.Key<=u&&u<p.Key.AddHours(3)) kv=p.Value;
			if(kv==null){ DateTime lat=DateTime.MinValue; foreach(KeyValuePair<DateTime,double> p in d.Kp) if(p.Key<=u&&p.Key>lat){lat=p.Key;kv=p.Value;} }
			if(kv!=null){ m.Kp=(int)Math.Round(kv.Value*10); any=true; }
		}
		return any;
	}
	// массовое дозаполнение: все записи, где хоть одно поле пусто
	public int BackfillWeather(DateTime? forceUntil=null){
		List<Measurement> targets=D.M.Where(m=>m.PressMm==null||m.TempC==null||m.Hum==null||m.Kp==null
			||(forceUntil!=null&&m.T<=forceUntil.Value)).OrderBy(m=>m.T).ToList();
		int n=0,done=0;
		JobProgress(0,targets.Count);
		foreach(IGrouping<DateTime,Measurement> grp in targets.GroupBy(m=>m.T.Date)){
			WxDay d=FetchWxDay(grp.Key); if(d==null){ done+=grp.Count(); JobProgress(done); continue; }
			foreach(Measurement m in grp){
				if(forceUntil!=null&&m.T<=forceUntil.Value){// принудительно: пишем заново поверх старого снимка
					float? op=m.PressMm,ot=m.TempC,oh=m.Hum; int? okp=m.Kp; string ow=m.Wx;
					m.PressMm=null;m.TempC=null;m.Hum=null;m.Kp=null;m.Wx="";
					bool got=false; try{ got=FillWx(m,d); }catch{}
					if(got){ AppendMeasurement(m); n++; }
					else{ m.PressMm=op;m.TempC=ot;m.Hum=oh;m.Kp=okp;m.Wx=ow; }// архив недоступен — оставляем как было
					System.Threading.Thread.Sleep(120);
					done++; JobProgress(done);
					continue;
				}
				try{ if(FillWx(m,d)){ AppendMeasurement(m); n++; } }catch{}
				System.Threading.Thread.Sleep(120);// щадим API
				done++; JobProgress(done);
			}
		}
		return n;
	}

	// ----------------------------- popup (прогноз выше нормы) -----------------------------
	// прогнозный Kp для момента (локальное время): 3-ч бин SWPC в UTC; null — нет прогноза
	int? KpForecastAt(DateTime localT){
		DateTime u=localT.ToUniversalTime();
		double? v=null;
		foreach(KeyValuePair<DateTime,double> p in KpForecast) if(p.Key<=u&&u<p.Key.AddHours(3)) v=p.Value;
		return v!=null?(int?)Math.Round(v.Value*10):null;
	}
	// прогноз НН по почасовому прогнозу погоды на ближайшие hours часов (для UI и popup)
	public JArray ForecastPredictions(int hours=12){
		JArray arr=new JArray();
		if(Forecast.Count==0) return arr;
		DateTime now=DateTime.Now;
		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		foreach(WxHour pt in Forecast.Where(p=>p.T>=now&&p.T<=now.AddHours(hours))){
			Measurement m=new Measurement{
				T=pt.T,
				PressMm=pt.Press!=null?(float)(pt.Press.Value*0.750062):(float?)null,
				TempC=pt.Temp!=null?(float)pt.Temp.Value:(float?)null,
				Hum=pt.Hum!=null?(float)pt.Hum.Value:(float?)null,
				Kp=KpForecastAt(pt.T)??Weather.Kp
			};
			double[] y=Forward(D.Nn,FeatsFor(m,intakes));
			double sys=Clamp(120+30*y[0],70,250),dia=Clamp(80+20*y[1],40,150);
			if(dia>=sys) dia=sys-20;
			arr.Add(new JObject{
				["t"]=pt.T.ToString("yyyy-MM-dd HH:00"),
				["sys"]=Math.Round(sys),["dia"]=Math.Round(dia),
				["press"]=pt.Press!=null?(JToken)Math.Round(pt.Press.Value*0.750062):JValue.CreateNull(),
				["temp"]=pt.Temp!=null?(JToken)Math.Round(pt.Temp.Value):JValue.CreateNull(),
				["kp"]=m.Kp!=null?(JToken)(m.Kp.Value/10.0):JValue.CreateNull()
			});
		}
		return arr;
	}
	// «через ~N часов условия как в дни ростов»: прогон модели по почасовому прогнозу
	// погоды/Kp и предупреждаем заранее, а не по факту. Общий cooldown с обычным popup.
	public void MaybePopupForecast(){
		if(Forecast.Count==0) return;
		lock(Sync){
		try{
			if(D.LastPopup!=null&&DateTime.Now-D.LastPopup.Value<TimeSpan.FromMinutes(Cfg.PopupCooldownMin)) return;
			DateTime now=DateTime.Now;
			foreach(JToken t in ForecastPredictions(12)){
				JObject o=(JObject)t;
				DateTime pt=DateTime.Parse((string)o["t"],CultureInfo.InvariantCulture,DateTimeStyles.None);
				double sys=(double)o["sys"],dia=(double)o["dia"];
				if(!(sys>Cfg.NormSys)&&!(dia>Cfg.NormDia)) continue;
				D.LastPopup=DateTime.Now;
				AppendPopup();
				int inH=Math.Max(1,(int)Math.Round((pt-now).TotalHours));
				string msg="Прогноз личной модели на ~"+inH+" ч: "+sys.ToString("0")+" / "+dia.ToString("0")+" мм рт.ст."
					+" (норма ≤ "+Cfg.NormSys+" / "+Cfg.NormDia+").\r\nУсловия похожи на те, при которых у вас росло давление — измерьте заранее."
					+(o["kp"]!=null&&(double)o["kp"]>=5?"\r\nПрогнозируется геомагнитная активность Kp "+((double)o["kp"]).ToString("0.0")+".":"");
				Notify("BpMonitor — прогноз",msg);
				break;
			}
		} catch(Exception ex){ Log("PopupForecast: "+ex.Message); }
		}
	}
	public void MaybePopup(string trigger){
		if(Weather.PressMm==null) return;
		lock(Sync){
		try{
			if(D.LastPopup!=null&&DateTime.Now-D.LastPopup.Value<TimeSpan.FromMinutes(Cfg.PopupCooldownMin)) return;
			(double sys,double dia) p=PredictNow();
			if(!(p.sys>Cfg.NormSys)&&!(p.dia>Cfg.NormDia)) return;
			D.LastPopup=DateTime.Now;
			AppendPopup();
			string msg="Внимание! Обнаружены внешние условия, схожие с повышением давления.\r\n"+$"Прогноз (личная модель): {p.sys:0} / {p.dia:0} мм рт.ст. (норма ≤ {Cfg.NormSys} / {Cfg.NormDia})\r\n"+$"Погода: {Weather.Text}, {Weather.PressMm?.ToString("0")} мм рт.ст., {Weather.TempC?.ToString("0")}°C. Рекомендуется выполнить измерение.";
			Notify("BpMonitor — внимание",msg);
		} catch{}
		}
	}
}
}
