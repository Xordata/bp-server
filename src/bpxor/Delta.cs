// Фаза 5: дельта-синхронизация. Курсор клиента — (gen, seq), где seq = байтовое смещение в
// append-журнале data.bin (монотонен внутри поколения), gen — поколение журнала: компакция
// Save() переписывает файл и повышает gen, клиент со старым gen получает reset и полный
// /api/state. Конфиг/НН/погода/медpk отдаются целиком при несовпадении ревизий (mtime файлов;
// погода — Updated). Ответ без изменений — несколько десятков байт.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
partial class Profile{
	public long BinGen{ get{ return _binGen; } }
	long _binGen;
	int _hdrSize=5;// v2: magic(4)+ver(1); v3: +gen(8)=13 — курсор since никогда не должен указывать в заголовок
	public long BinSeq{ get{ lock(Io){ return File.Exists(DataBinPath)?new FileInfo(DataBinPath).Length:0; } } }
	long RevCfg{ get{ try{ return File.Exists(CfgPath)?File.GetLastWriteTimeUtc(CfgPath).Ticks:0; } catch{ return 0; } } }
	long RevNn{ get{ try{ return File.Exists(NnPath)?File.GetLastWriteTimeUtc(NnPath).Ticks:0; } catch{ return 0; } } }
	long RevMedPk{ get{ try{ return File.Exists(MedPkPath)?File.GetLastWriteTimeUtc(MedPkPath).Ticks:0; } catch{ return 0; } } }
	long RevWx{ get{ DateTime? u=Weather.Updated; return u!=null?u.Value.Ticks:0; } }
	public JObject SyncJson(){
		return new JObject{
			["gen"]=_binGen,["seq"]=BinSeq,["cfgRev"]=RevCfg,["nnRev"]=RevNn,["wxRev"]=RevWx,["pkRev"]=RevMedPk};
	}
	// ETag снимка: совпал — 304 без тела
	public string SyncEtag{ get{
		JObject s=SyncJson();
		return "\"" + s["gen"].ToString() + "-" + s["seq"].ToString() + "-" + s["cfgRev"].ToString() + "-" + s["nnRev"].ToString() + "-" + s["wxRev"].ToString() + "-" + s["pkRev"].ToString() + "\"";
	} }
	// since/gen — курсор клиента; cfgRev/nnRev/wxRev/pkRev — его ревизии. Ответ:
	// {reset} | {changed:false,seq,...} | {changed:true,seq,...,meas/measDel/...,cfg?,nn?,weather?,medPkDb?}
	public JObject DeltaJson(long since,long gen,long cfgRev,long nnRev,long wxRev,long pkRev){
		JObject r=new JObject();
		byte[] range=null; Dictionary<int,string> rev=null; long seq; bool readFail=false;
		lock(Io){
			long len=File.Exists(DataBinPath)?new FileInfo(DataBinPath).Length:0;
			seq=len;
			if(gen!=_binGen||since<0||since>len){ r["reset"]=true; r["gen"]=_binGen; r["seq"]=len; return r; }
			if(since<_hdrSize) since=_hdrSize;// клиент до первого замера: курсор 0 -> старт после заголовка
			if(since<len){
				range=new byte[len-since]; int got=0;
				try{
					using(FileStream fs=File.OpenRead(DataBinPath)){
						fs.Seek(since,SeekOrigin.Begin);
						while(got<range.Length){ int rd2=fs.Read(range,got,range.Length-got); if(rd2<=0) break; got+=rd2; }
					}
				} catch{ readFail=true; }
				if(got<range.Length) readFail=true;// гонка с компакцией: файл усёкся во время чтения
				if(!readFail){
					rev=new Dictionary<int,string>();
					foreach(KeyValuePair<string,int> kv in _pool) rev[kv.Value]=kv.Key;
				}
			}
		}
		if(readFail){ r["reset"]=true; r["gen"]=_binGen; r["seq"]=BinSeq; return r; }
		bool changed=false;
		if(range!=null&&range.Length>0) changed=DecodeDelta(range,rev,r);
		long c=RevCfg,n=RevNn,w=RevWx,pk=RevMedPk;
		r["seq"]=seq; r["gen"]=_binGen; r["cfgRev"]=c; r["nnRev"]=n; r["wxRev"]=w; r["pkRev"]=pk;
		if(cfgRev!=c){ r["cfg"]=MaskToken(JObject.FromObject(Cfg)); r["medPk"]=MedPkJson(); changed=true; }
		if(nnRev!=n||wxRev!=w){ r["nn"]=NnJson(); changed=true; }
		if(wxRev!=w){ r["weather"]=WeatherJson(); changed=true; }
		if(pkRev!=pk){ r["medPkDb"]=JObject.FromObject(MedPkDb); changed=true; }
		r["changed"]=changed;
		return r;
	}
	// разбор диапазона журнала в JSON-массивы (та же форма, что в /api/state: PascalCase)
	bool DecodeDelta(byte[] buf,Dictionary<int,string> pool,JObject r){
		JArray meas=null,measDel=null,emo=null,emoDel=null,noi=null,noiDel=null,sleep=null,sleepDel=null,mt=null,mtDel=null;
		bool any=false;
		using(BinaryReader rd=new BinaryReader(new MemoryStream(buf))){
			try{
				while(rd.BaseStream.Position<rd.BaseStream.Length){
					byte type=rd.ReadByte(); int len=rd.ReadInt32();
					if(len<0||rd.BaseStream.Position+len>rd.BaseStream.Length) break;// оборванный хвост
					byte[] pl=rd.ReadBytes(len);
					using(BinaryReader p=new BinaryReader(new MemoryStream(pl))){
						if(type==RecMeas){ any=true; Measurement m=ReadMeas(p,pool); (meas??(meas=new JArray())).Add(JObject.FromObject(m)); }
						else if(type==RecMeasDel){ any=true; (measDel??(measDel=new JArray())).Add(new Guid(p.ReadBytes(16)).ToString("N")); }
						else if(type==RecEmo){ any=true; (emo??(emo=new JArray())).Add(JObject.FromObject(ReadEmo(p))); }
						else if(type==RecEmoDel){ any=true; (emoDel??(emoDel=new JArray())).Add(new Guid(p.ReadBytes(16)).ToString("N")); }
						else if(type==RecNoi){ any=true; (noi??(noi=new JArray())).Add(JObject.FromObject(ReadNoi(p,pool))); }
						else if(type==RecNoiDel){ any=true; (noiDel??(noiDel=new JArray())).Add(new Guid(p.ReadBytes(16)).ToString("N")); }
						else if(type==RecSleep){ any=true; (sleep??(sleep=new JArray())).Add(JObject.FromObject(ReadSleep(p))); }
						else if(type==RecSleepDel){ any=true; (sleepDel??(sleepDel=new JArray())).Add(new Guid(p.ReadBytes(16)).ToString("N")); }
						else if(type==RecMedTake){ any=true; (mt??(mt=new JArray())).Add(JObject.FromObject(ReadMedTake(p,pool))); }
						else if(type==RecMedTakeDel){ any=true; (mtDel??(mtDel=new JArray())).Add(new Guid(p.ReadBytes(16)).ToString("N")); }
						// RecStr/RecPopup — в дельте не нужны (пул уже у клиента, popup — внутреннее состояние)
					}
				}
			} catch{}
		}
		if(meas!=null) r["meas"]=meas; if(measDel!=null) r["measDel"]=measDel;
		if(emo!=null) r["emo"]=emo; if(emoDel!=null) r["emoDel"]=emoDel;
		if(noi!=null) r["noi"]=noi; if(noiDel!=null) r["noiDel"]=noiDel;
		if(sleep!=null) r["sleep"]=sleep; if(sleepDel!=null) r["sleepDel"]=sleepDel;
		if(mt!=null) r["medtake"]=mt; if(mtDel!=null) r["medtakeDel"]=mtDel;
		return any;
	}
	// окно истории: новые-first, строго раньше before; hasMore — есть ли ещё старше
	public JObject MeasurementsWindow(DateTime before,int limit){
		List<Measurement> l=D.M.Where(m=>m.T<before).OrderByDescending(m=>m.T).Take(limit+1).ToList();
		bool more=l.Count>limit;
		return new JObject{
			["items"]=JArray.FromObject(l.Take(limit).ToList()),
			["hasMore"]=more,
			["total"]=D.M.Count};
	}
}
}
