// Фаза 7: синхронизация десктоп<->веб (PLAN §14). Персональные API-токены (показываются
// один раз, на диске только SHA256-хэш) + транспорт push/pull по append-журналу.
// Merge = last-writer-wins по серверному seq: сервер аппендит входящие записи в свой журнал,
// порядок применения и есть порядок версий. Echo-подавление: запись с тем же Id и содержимым
// не аппендится повторно; тумстоун уже удалённого Id — тоже (иначе pull->push гонял бы дубли по кругу).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{

public class ApiTok{
	public string Id;// короткий id для списка/отзыва
	public string Uid;
	public string Hash;// SHA256(id.secret) — сам токен на сервере не хранится
	public string Name;
	public DateTime Created;
	public DateTime LastUsed;
}

static class ApiTokens{
	static readonly object Lk=new object();
	static Dictionary<string,ApiTok> _map=new Dictionary<string,ApiTok>();// id -> токен
	static string _path;
	static string B64(int n){ byte[] b=new byte[n]; RandomNumberGenerator.Fill(b); return Convert.ToBase64String(b).Replace('+','-').Replace('/','_').TrimEnd('='); }
	static string Hash(string s){ using(SHA256 h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-",""); }
	public static void Init(string dataRoot){
		_path=Path.Combine(dataRoot,"tokens.json");
		try{
			if(File.Exists(_path)){
				List<ApiTok> l=JsonConvert.DeserializeObject<List<ApiTok>>(File.ReadAllText(_path));
				_map=l!=null?l.ToDictionary(t=>t.Id,t=>t):new Dictionary<string,ApiTok>();
			}
		}catch{ _map=new Dictionary<string,ApiTok>(); }
	}
	static void Save(){ lock(Lk){ try{ File.WriteAllText(_path,JsonConvert.SerializeObject(_map.Values.ToList(),Formatting.Indented)); }catch{} } }
	// (id, token) — token показывается пользователю РОВНО ОДИН РАЗ
	public static (string Id,string Token) Create(string uid,string name){
		string id=B64(6), secret=B64(32);
		ApiTok t=new ApiTok{Id=id,Uid=uid,Hash=Hash(id+"."+secret),Name=string.IsNullOrWhiteSpace(name)?"устройство":name.Trim(),Created=DateTime.Now};
		lock(Lk){ _map[id]=t; }
		Save();
		return (id,id+"."+secret);
	}
	public static string Resolve(string token){
		if(string.IsNullOrEmpty(token)) return null;
		int dot=token.IndexOf('.'); if(dot<1) return null;
		string id=token.Substring(0,dot);
		lock(Lk){
			if(!_map.TryGetValue(id,out ApiTok t)) return null;
			if(t.Hash!=Hash(token)) return null;
			t.LastUsed=DateTime.Now;// не пишем на диск каждый запрос — только при Create/Revoke
			return t.Uid;
		}
	}
	public static bool Revoke(string uid,string id){
		lock(Lk){ if(!_map.TryGetValue(id,out ApiTok t)||t.Uid!=uid) return false; _map.Remove(id); }
		Save(); return true;
	}
	public static JArray List(string uid){
		JArray a=new JArray();
		lock(Lk){
			foreach(ApiTok t in _map.Values.Where(x=>x.Uid==uid).OrderBy(x=>x.Created))
				a.Add(new JObject{["id"]=t.Id,["name"]=t.Name,["created"]=t.Created.ToString("yyyy-MM-dd HH:mm"),
					["lastUsed"]=t.LastUsed==default(DateTime)?"":t.LastUsed.ToString("yyyy-MM-dd HH:mm")});
		}
		return a;
	}
}

partial class Profile{
	// Guid'ы удалённых записей ("N"): собираются из тумстоунов в LoadBin и при правках.
	// Нужны для echo-подавления тумстоунов (см. SyncDel).
	readonly HashSet<string> _dead=new HashSet<string>();
	static string NormId(string id){ Guid g; return Guid.TryParse(id,out g)?g.ToString("N"):(id??""); }
	static string H(object v){ return v==null?"":Convert.ToString(v,System.Globalization.CultureInfo.InvariantCulture); }
	// каноническая сигнатура записи для «то же содержимое» (echo). EmotionLabel исключён — он производный от EmotionV
	static string MeasHash(Measurement m){
		return string.Join("|",m.T.Ticks,m.Sys,m.Dia,m.Pulse,H(m.PressMm),H(m.TempC),H(m.Hum),H(m.Kp),H(m.EmotionV),H(m.SleepH),H(m.SleepQ),H(m.PosArm),m.Wx??"",m.Note??"",
			string.Join(",",(m.Meds??new List<string>()).OrderBy(x=>x)),string.Join(",",(m.Noise??new List<string>()).OrderBy(x=>x)));
	}
	static string EmoHash(EmotionEntry e){ return string.Join("|",e.T.Ticks,e.Val,e.Note??""); }
	static string NoiHash(NoiseEntry n){ return string.Join("|",n.T.Ticks,n.Type??"",n.Note??""); }
	static string SleepHash(SleepEntry s){ return string.Join("|",H(s.Hours),s.Quality,s.Note??"",s.T.Ticks); }
	static string MtHash(MedTakeEntry mt){ return string.Join("|",mt.T.Ticks,mt.Med??""); }
	static string SaltHash(SaltEntry st){ return string.Join("|",st.T.Ticks,st.Val,st.Note??""); }
	bool SyncDel<T>(string id,List<T> list,Func<T,string> getid,byte delType){
		id=NormId(id);
		if(id=="") return false;
		if(!list.Any(x=>getid(x)==id)&&_dead.Contains(id)) return false;// echo тумстоуна
		list.RemoveAll(x=>getid(x)==id);
		_dead.Add(id);
		AppendGuidRec(delType,id);
		return true;
	}
	// POST /api/sync/push {recs:[{k:"meas"|"measDel"|"emo"|"emoDel"|"noi"|"noiDel"|"sleep"|"sleepDel"|"mt"|"mtDel"|"salt"|"saltDel",...}], cfg?:{...}}
	// Вызывается под lock(Sync) (обёртка Post). Записи применяются КАК ЕСТЬ: снимки погоды/контекста
	// уже вычислены на стороне-источнике, переснимать нельзя — история перезапишется сегодняшней погодой.
	// Train/SaveNn — один раз на пачку (первый импорт сотен записей не должен учить модель сотни раз).
	// MaybePopup не вызываем: источник уже уведомил своего пользователя, дубли не нужны.
	public JObject SyncPush(JObject req){
		int applied=0,skipped=0; bool touched=false;
		JArray recs=req["recs"] as JArray;
		if(recs!=null) foreach(JToken jt in recs){
			string k=(string)jt["k"]??"";
			if(k=="meas"){
				Measurement m=jt.ToObject<Measurement>(); m.Id=NormId(m.Id);
				if(m.Id==""||m.T==default(DateTime)){ skipped++; continue; }
				Measurement ex=D.M.FirstOrDefault(x=>x.Id==m.Id);
				if(ex!=null&&MeasHash(ex)==MeasHash(m)){ skipped++; continue; }
				D.M.RemoveAll(x=>x.Id==m.Id); D.M.Add(m); AppendMeasurement(m); applied++; touched=true;
			}
			else if(k=="measDel"){ if(SyncDel((string)jt["Id"],D.M,x=>x.Id,RecMeasDel)){ applied++; touched=true; } else skipped++; }
			else if(k=="emo"){
				EmotionEntry e=jt.ToObject<EmotionEntry>(); e.Id=NormId(e.Id);
				if(e.Id==""||e.T==default(DateTime)){ skipped++; continue; }
				EmotionEntry ex=D.E.FirstOrDefault(x=>x.Id==e.Id);
				if(ex!=null&&EmoHash(ex)==EmoHash(e)){ skipped++; continue; }
				D.E.RemoveAll(x=>x.Id==e.Id); D.E.Add(e); AppendEmotion(e); applied++; touched=true;
			}
			else if(k=="emoDel"){ if(SyncDel((string)jt["Id"],D.E,x=>x.Id,RecEmoDel)){ applied++; touched=true; } else skipped++; }
			else if(k=="noi"){
				NoiseEntry n=jt.ToObject<NoiseEntry>(); n.Id=NormId(n.Id);
				if(n.Id==""||n.T==default(DateTime)){ skipped++; continue; }
				NoiseEntry ex=D.N.FirstOrDefault(x=>x.Id==n.Id);
				if(ex!=null&&NoiHash(ex)==NoiHash(n)){ skipped++; continue; }
				D.N.RemoveAll(x=>x.Id==n.Id); D.N.Add(n); AppendNoise(n); applied++; touched=true;
			}
			else if(k=="noiDel"){ if(SyncDel((string)jt["Id"],D.N,x=>x.Id,RecNoiDel)){ applied++; touched=true; } else skipped++; }
			else if(k=="sleep"){
				SleepEntry s=jt.ToObject<SleepEntry>(); s.Id=NormId(s.Id);
				if(s.Id==""||s.T==default(DateTime)){ skipped++; continue; }
				SleepEntry ex=D.S.FirstOrDefault(x=>x.Id==s.Id);
				if(ex!=null&&SleepHash(ex)==SleepHash(s)){ skipped++; continue; }
				D.S.RemoveAll(x=>x.Id==s.Id); D.S.Add(s); AppendSleep(s); applied++; touched=true;
			}
			else if(k=="sleepDel"){ if(SyncDel((string)jt["Id"],D.S,x=>x.Id,RecSleepDel)){ applied++; touched=true; } else skipped++; }
			else if(k=="salt"){
				SaltEntry st=jt.ToObject<SaltEntry>(); st.Id=NormId(st.Id);
				if(st.Id==""||st.T==default(DateTime)||st.Val<1||st.Val>5){ skipped++; continue; }
				SaltEntry ex5=D.Salt.FirstOrDefault(x=>x.Id==st.Id);
				if(ex5!=null&&SaltHash(ex5)==SaltHash(st)){ skipped++; continue; }
				D.Salt.RemoveAll(x=>x.Id==st.Id); D.Salt.Add(st); AppendSalt(st); applied++;
			}
			else if(k=="saltDel"){ if(SyncDel((string)jt["Id"],D.Salt,x=>x.Id,RecSaltDel)){ applied++; } else skipped++; }
			else if(k=="mt"){
				MedTakeEntry mt=jt.ToObject<MedTakeEntry>(); mt.Id=NormId(mt.Id);
				if(mt.Id==""||mt.T==default(DateTime)){ skipped++; continue; }
				MedTakeEntry ex=D.MedTakes.FirstOrDefault(x=>x.Id==mt.Id);
				if(ex!=null&&MtHash(ex)==MtHash(mt)){ skipped++; continue; }
				D.MedTakes.RemoveAll(x=>x.Id==mt.Id); D.MedTakes.Add(mt); AppendMedTake(mt); applied++; touched=true;
			}
			else if(k=="mtDel"){ if(SyncDel((string)jt["Id"],D.MedTakes,x=>x.Id,RecMedTakeDel)){ applied++; touched=true; } else skipped++; }
			else skipped++;// неизвестный вид — пропущен (задел на будущее)
		}
		if(req["cfg"] is JObject cfgJ) MergeCfg(cfgJ);// односторонняя синхронизация конфига (ведущая сторона выбирается на десктопе)
		if(touched){ try{ Train(); }catch{} SaveNn(); }
		return new JObject{["ok"]=true,["applied"]=applied,["skipped"]=skipped,["gen"]=_binGen,["seq"]=BinSeq,["cfgRev"]=RevCfg};
	}
	// Конфиг из push: тот же merge, что /api/config (Replace для массивов), но порт и HA-токен бережём.
	void MergeCfg(JObject body){
		int oldPort=Cfg.Port;
		string oldToken=Cfg.Ha!=null?Cfg.Ha.Token:"";
		Config nc=JsonConvert.DeserializeObject<Config>(JsonConvert.SerializeObject(Cfg),new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace});
		JsonConvert.PopulateObject(body.ToString(Formatting.None),nc,new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace});
		nc.Port=oldPort;
		if(nc.Ha!=null&&nc.Ha.Token==TokenMask) nc.Ha.Token=oldToken;
		nc.Meds=nc.Meds.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		nc.Noises=nc.Noises.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		Cfg=nc; SaveCfg(); EnsureNnDim();
	}
	// GET /api/sync/pull?since=&gen=&cfgRev= — дельта журнала + cfg при несовпадении ревизии.
	// Ревизии nn/wx/pk передаём текущими: модель НН и погода на десктоп в v1 не едут.
	public JObject SyncPull(long since,long gen,long cfgRev){
		return DeltaJson(since,gen,cfgRev,RevNn,RevWx,RevMedPk);
	}
}
}
