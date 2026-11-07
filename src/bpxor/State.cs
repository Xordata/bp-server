// Снимок состояния для UI (перенесено из десктопного HTTP-блока)
// Фаза 5: measurements отдаются окном (последние 500, остальное — /api/measurements?before=),
// в снимке добавлен sync-блок (курсор дельта-синхронизации).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
partial class Profile{
	public const int StateMeasWindow=500;
	public JObject StateJson(){
		List<Measurement> allMeas=D.M.OrderByDescending(m=>m.T).ToList();
		return new JObject{
			["medPk"]=MedPkJson(),
			["cfg"]=MaskToken(JObject.FromObject(Cfg)),
			["autostart"]=GetAutostart(),
			["weather"]=WeatherJson(),
			["measurements"]=JArray.FromObject(allMeas.Take(StateMeasWindow).ToList()),
			["measTotal"]=allMeas.Count,
			["wxMissing"]=allMeas.Count(x=>x.PressMm==null||x.TempC==null||x.Hum==null||x.Kp==null),
			["sync"]=SyncJson(),
			["emotions"]=JArray.FromObject(D.E.OrderByDescending(e=>e.T).ToList()),
			["noises"]=JArray.FromObject(D.N.OrderByDescending(e=>e.T).ToList()),
			["sleeps"]=JArray.FromObject(D.S.OrderByDescending(e=>e.T).ToList()),
			["salts"]=JArray.FromObject(D.Salt.OrderByDescending(e=>e.T).ToList()),
			["medtakes"]=JArray.FromObject(D.MedTakes.OrderByDescending(x=>x.T).Take(500).ToList()),
			["medPkDb"]=JObject.FromObject(MedPkDb),
			["nn"]=NnJson()
		};
	}
	public JObject WeatherJson(){
		double kpFc=0; DateTime u0=DateTime.UtcNow;
		foreach(KeyValuePair<DateTime,double> fp in KpForecast) if(fp.Key>=u0&&fp.Key<=u0.AddHours(24)&&fp.Value>kpFc) kpFc=fp.Value;
		JObject wxJ=JObject.FromObject(Weather);
		wxJ["kpForecast"]=Math.Round(kpFc,1);// прогнозный Kp (макс за 24 ч), 0 = нет данных
		return wxJ;
	}
	public JObject MedPkJson(){
		return JObject.FromObject(Cfg.Meds.GroupBy(med=>med).ToDictionary(g=>g.Key,g=>{var kt=MedKtFor(g.Key);return new{half=kt.half,dur=kt.dur,known=MedPkDb.Any(kv=>NormMed(g.Key).Contains(kv.Key))};}));
	}
	public JObject NnJson(){
		(double sys,double dia) p=PredictNow();
		return new JObject{
			["W1"]=JArray.FromObject(D.Nn.W1),["B1"]=JArray.FromObject(D.Nn.B1),["W1b"]=JArray.FromObject(D.Nn.W1b),["B1b"]=JArray.FromObject(D.Nn.B1b),
			["W2"]=JArray.FromObject(D.Nn.W2),["B2"]=JArray.FromObject(D.Nn.B2),
			["loss"]=JArray.FromObject(D.Nn.Loss.Skip(Math.Max(0,D.Nn.Loss.Count-300))),
			["predict"]=new JObject{["sys"]=Math.Round(p.sys,1),["dia"]=Math.Round(p.dia,1)},
			["forecast"]=ForecastPredictions(12),
			["validate"]=_lastValidation,
			["tune"]=_lastTune
		};
	}
	public JArray Predictions(){
		JArray arr=new JArray();
		foreach(Measurement m in D.M.OrderBy(m=>m.T)){
			double[] y=Forward(D.Nn,FeatsFor(m));
			arr.Add(new JObject{["id"]=m.Id,["ps"]=Math.Round(Clamp(120+30*y[0],70,250),1),["pd"]=Math.Round(Clamp(80+20*y[1],40,150),1)});
		}
		return arr;
	}
	JObject MaskToken(JObject cfg){
		JToken t;
		if(cfg["Ha"]!=null&&cfg["Ha"]["Token"]!=null&&(t=cfg["Ha"]["Token"]).ToString()!="")
			cfg["Ha"]["Token"]=TokenMask;
		return cfg;
	}
}
}
