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
// Хранилище: config.json + бинарный append-журнал data.bin + nn.bin (порт BpMonitor.cs)
partial class Profile
{
	// ----------------------------- пути/загрузка -----------------------------
	public void InitPaths(string dir=null){// dir != null — работа в песочнице (selftest)
		ExeDir=dir??AppDomain.CurrentDomain.BaseDirectory;
		CfgPath=Path.Combine(ExeDir,"config.json");
		DataPath=Path.Combine(ExeDir,"data","data.json");
		DataBinPath=Path.Combine(ExeDir,"data","data.bin");
		IndexPath=Path.Combine(AppContext.BaseDirectory,"index.html");// SPA рядом с бинарём, данные — в ExeDir (--data)
	}
	public void Load(){
		LoadMedPk();// справочник ФК нужен до EnsureNnDim/Train (PK-фичи)
		LoadValidation();
		LoadTune();
		try{
			if(File.Exists(CfgPath))
				Cfg=JsonConvert.DeserializeObject<Config>(File.ReadAllText(CfgPath,Encoding.UTF8),JSONSettings)??new Config();
				Cfg.Meds=Cfg.Meds.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();// самолечение: дедуп списка лекарств без учёта регистра
				Cfg.Noises=Cfg.Noises.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();// дедуп шумов (старые конфиги содержали 4x дубли до фикса Replace)
				if(Cfg.MedKt!=null){ Dictionary<string,MedKt> mk=new Dictionary<string,MedKt>(); foreach(KeyValuePair<string,MedKt> kv in Cfg.MedKt) if(!string.IsNullOrWhiteSpace(kv.Key)) mk[NormMed(kv.Key)]=kv.Value; Cfg.MedKt=mk; }
		} catch{ Quarantine(CfgPath); Cfg=new Config(); }
		bool migrated=false,nnLoaded=false;
		string dataFile=null;
		try{
			if(File.Exists(DataBinPath)&&IsBpmn(DataBinPath)){// v2: бинарный журнал
				LoadBin();
				nnLoaded=LoadNn();
			}else if(File.Exists(DataBinPath)){// v1: gzip-JSON — мигрируем в бинарный журнал
				using(FileStream fs=File.OpenRead(DataBinPath))
				using(GZipStream gz=new GZipStream(fs,CompressionMode.Decompress))
				using(StreamReader sr=new StreamReader(gz,Encoding.UTF8))
					D=JsonConvert.DeserializeObject<DataStore>(sr.ReadToEnd())??new DataStore();
				migrated=true;
			}else if(File.Exists(DataPath)){// v0: простой JSON
				D=JsonConvert.DeserializeObject<DataStore>(File.ReadAllText(DataPath,Encoding.UTF8))??new DataStore();
				migrated=true; dataFile=DataPath;
			}
		} catch{ Quarantine(dataFile??DataBinPath); D=new DataStore(); }// битый файл спасён от перезаписи
		if(Cfg==null) Cfg=new Config();
		if(D==null) D=new DataStore();
		bool kpChanged=MigrateKp();
		EnsureNnDim();		if(migrated||kpChanged){
			Save();// полный снимок: переезд на бинарный формат
			if(dataFile!=null) try{ File.Move(dataFile,dataFile+".old-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch{}
		}
		else if(!nnLoaded) SaveNn();
		else if(_logRecords>2*(D.M.Count+D.E.Count+D.N.Count+D.S.Count)+64)
			Save();// компакция: накопилось слишком много tombstone/обновлений
	}
	void Quarantine(string path){
		try{ File.Move(path,path+".bad-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch{}
	}
	// Суточный бэкап: config.json + data.bin + nn.bin -> backups\yyyyMMdd.zip (не чаще раза в сутки,
	// храним последние 30 архивов). Вызывается при старте и раз в час из UI-таймера.
	public void DailyBackup(){
		try{
			string dir=Path.Combine(ExeDir,"backups");
			string zip=Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd")+".zip");
			if(File.Exists(zip)) return;
			Directory.CreateDirectory(dir);
			string tmp=zip+".tmp";
			using(ZipArchive z=new ZipArchive(File.Create(tmp),ZipArchiveMode.Create)){
				if(File.Exists(CfgPath)) z.CreateEntryFromFile(CfgPath,"config.json");
				if(File.Exists(DataBinPath)) z.CreateEntryFromFile(DataBinPath,"data.bin");
				string nn=Path.Combine(ExeDir,"data","nn.bin");
				if(File.Exists(nn)) z.CreateEntryFromFile(nn,"nn.bin");
			}
			if(File.Exists(zip)) File.Delete(zip);
			File.Move(tmp,zip);
			foreach(FileInfo f in new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f2=>f2.Name).Skip(30))
				try{ f.Delete(); }catch{}
			Log("backup: "+Path.GetFileName(zip));
		} catch(Exception ex){ Log("backup error: "+ex.Message); }
	}
	// Kp во всех источниках — десятые доли (33 = 3.3). Старые записи: GFZ писал Kp*100, HA — 0..9
	bool MigrateKp(){
		bool changed=false;
		foreach(Measurement m in D.M){
			if(m.Kp==null) continue;
			int v=m.Kp.Value,nv=v;
			if(v>90) nv=v/10;
			else if(v>=0&&v<=9) nv=v*10;
			if(nv!=v){ m.Kp=nv; changed=true; }
		}
		return changed;
	}
	public void Save(){// полный снимок: миграция/компакция/выход
		lock(Io){
			try{
				File.WriteAllText(CfgPath,JsonConvert.SerializeObject(Cfg,Formatting.Indented));
				Directory.CreateDirectory(Path.GetDirectoryName(DataBinPath)??"");
				string tmp=DataBinPath+".tmp";
				_pool.Clear(); _poolNext=0;
				using(BinaryWriter w=new BinaryWriter(File.Create(tmp))){
					w.Write(BinMagic); w.Write(BinVer);
					Func<string,int> pool=PoolWriter(w);
					foreach(Measurement m in D.M.OrderBy(x=>x.T)) WriteRec(w,RecMeas,MeasPayload(m,pool));
					foreach(EmotionEntry e in D.E.OrderBy(x=>x.T)) WriteRec(w,RecEmo,EmoPayload(e,pool));
					foreach(NoiseEntry n2 in D.N.OrderBy(x=>x.T)) WriteRec(w,RecNoi,NoiPayload(n2,pool));
					foreach(SleepEntry s2 in D.S.OrderBy(x=>x.T)) WriteRec(w,RecSleep,SleepPayload(s2,pool));
					foreach(MedTakeEntry mt in D.MedTakes.OrderBy(x=>x.T)) WriteRec(w,RecMedTake,MedTakePayload(mt,pool));
					if(D.LastPopup!=null) WriteRec(w,RecPopup,RecPayload(w2=>w2.Write(D.LastPopup.Value.Ticks)));
				}
				if(File.Exists(DataBinPath)) File.Delete(DataBinPath);
				File.Move(tmp,DataBinPath);
			} catch{
				/* В/В не критичен для живости UI */
			}
		}
		SaveNn();
	}
	public void SaveCfg(){
		lock(Io){ try{ File.WriteAllText(CfgPath,JsonConvert.SerializeObject(Cfg,Formatting.Indented)); } catch{} }
	}
	public void SaveNn(){// модель маленькая (~1-8 КБ) — переписываем целиком
		lock(Io){
			try{
				Directory.CreateDirectory(Path.GetDirectoryName(NnPath)??"");
				string tmp=NnPath+".tmp";
				NnModel n=D.Nn;
				using(BinaryWriter w=new BinaryWriter(File.Create(tmp))){
					w.Write(BinMagic); w.Write(BinVer); w.Write((byte)3);// формат весов v3: 2 скрытых слоя
					int h=n.W1.GetLength(0),f=n.W1.GetLength(1),h2=n.W1b.GetLength(0);
					w.Write(h); w.Write(f); w.Write(h2);
					for(int i=0;i<h;i++) for(int j=0;j<f;j++) w.Write(n.W1[i,j]);
					for(int i=0;i<h;i++) w.Write(n.B1[i]);
					for(int i=0;i<h2;i++) for(int j=0;j<h;j++) w.Write(n.W1b[i,j]);
					for(int i=0;i<h2;i++) w.Write(n.B1b[i]);
					for(int o=0;o<2;o++) for(int i=0;i<h2;i++) w.Write(n.W2[o,i]);
					for(int o=0;o<2;o++) w.Write(n.B2[o]);
					w.Write(n.Loss.Count);
					foreach(LossPt lp in n.Loss){
						DateTime t;
						if(!DateTime.TryParseExact(lp.T,"MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out t)) t=DateTime.MinValue;
						w.Write(t.Ticks); w.Write(lp.L);
					}
				}
				if(File.Exists(NnPath)) File.Delete(NnPath);
				File.Move(tmp,NnPath);
			} catch{}
		}
	}
	// ----------------------------- бинарное хранилище v2: append-журнал -----------------------------
	// data.bin = заголовок + поток записей [byte тип][int длина][payload]. Новая запись — дописка
	// нескольких десятков байт, файл целиком не переписывается. Строки — через пул (ushort id),
	// числа — типизированно (битовые флаги присутствия/ushort/float/sbyte). Полная перезапись
	// data.bin только при компакции/миграции. nn.bin — модель, маленькая, переписывается всегда.
	const uint BinMagic=0x4E4D5042;// "BPMN"
	const byte BinVer=2;
	public const byte RecStr=1,RecMeas=0x10,RecMeasDel=0x11,RecEmo=0x20,RecEmoDel=0x21,RecNoi=0x30,RecNoiDel=0x31,RecSleep=0x40,RecSleepDel=0x41,RecPopup=0x50,RecMedTake=0x60,RecMedTakeDel=0x61;
	Dictionary<string,int> _pool=new Dictionary<string,int>();
	int _poolNext;
	int _logRecords;
	string NnPath{ get{ return Path.Combine(ExeDir,"data","nn.bin"); } }
	byte[] RecPayload(Action<BinaryWriter> fill){
		using(MemoryStream ms=new MemoryStream()){
			using(BinaryWriter pw=new BinaryWriter(ms)) fill(pw);
			return ms.ToArray();
		}
	}
	void WriteRec(BinaryWriter w,byte type,byte[] payload){ w.Write(type); w.Write(payload.Length); w.Write(payload); }
	Func<string,int> PoolWriter(BinaryWriter w){// новая строка -> Str-запись в этот же writer
		return str=>{
			str=str??"";
			int id;
			if(_pool.TryGetValue(str,out id)) return id;
			id=_poolNext++; _pool[str]=id;
			WriteRec(w,RecStr,RecPayload(pw=>{ pw.Write((ushort)id); pw.Write(str); }));
			return id;
		};
	}
	byte[] MeasPayload(Measurement m,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(m.Id,out g);
			w.Write(g.ToByteArray());// 16
			w.Write(m.T.Ticks);// 8
			w.Write((ushort)m.Sys); w.Write((ushort)m.Dia); w.Write((ushort)m.Pulse);// 6
			byte fl=0;// биты присутствия опциональных полей
			if(m.PressMm!=null)fl|=1; if(m.TempC!=null)fl|=2; if(m.Hum!=null)fl|=4; if(m.Kp!=null)fl|=8;
			if(m.EmotionV!=null)fl|=16; if(m.SleepH!=null)fl|=32; if(m.SleepQ!=null)fl|=64;
			w.Write(fl);
			if(m.PressMm!=null)w.Write(m.PressMm.Value);
			if(m.TempC!=null)w.Write(m.TempC.Value);
			if(m.Hum!=null)w.Write(m.Hum.Value);
			if(m.Kp!=null)w.Write((short)m.Kp.Value);
			if(m.EmotionV!=null)w.Write((sbyte)m.EmotionV.Value);
			if(m.SleepH!=null)w.Write(m.SleepH.Value);
			if(m.SleepQ!=null)w.Write((byte)m.SleepQ.Value);
			w.Write((ushort)(string.IsNullOrEmpty(m.Wx)?0xFFFF:pool(m.Wx)));
			w.Write((byte)Math.Min(255,m.Meds.Count));
			foreach(string s3 in m.Meds.Take(255)) w.Write((ushort)pool(s3));
			w.Write((byte)Math.Min(255,m.Noise.Count));
			foreach(string s3 in m.Noise.Take(255)) w.Write((ushort)pool(s3));
			byte[] nb=Encoding.UTF8.GetBytes(m.Note??"");
			w.Write((ushort)Math.Min(65535,nb.Length)); w.Write(nb,0,Math.Min(65535,nb.Length));
		});
	}
	byte[] EmoPayload(EmotionEntry e,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(e.Id,out g);
			w.Write(g.ToByteArray()); w.Write(e.T.Ticks); w.Write((sbyte)e.Val);
			byte[] nb=Encoding.UTF8.GetBytes(e.Note??""); w.Write((ushort)nb.Length); w.Write(nb);
		});
	}
	byte[] NoiPayload(NoiseEntry n2,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(n2.Id,out g);
			w.Write(g.ToByteArray()); w.Write(n2.T.Ticks);
			w.Write((ushort)pool(n2.Type??""));
			byte[] nb=Encoding.UTF8.GetBytes(n2.Note??""); w.Write((ushort)nb.Length); w.Write(nb);
		});
	}
	byte[] SleepPayload(SleepEntry s2,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(s2.Id,out g);
			w.Write(g.ToByteArray()); w.Write(s2.T.Ticks); w.Write(s2.Hours); w.Write((byte)s2.Quality);
			byte[] nb=Encoding.UTF8.GetBytes(s2.Note??""); w.Write((ushort)nb.Length); w.Write(nb);
		});
	}
	byte[] MedTakePayload(MedTakeEntry mt,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(mt.Id,out g);
			w.Write(g.ToByteArray()); w.Write(mt.T.Ticks);
			w.Write((ushort)pool(mt.Med??""));
		});
	}
	BinaryWriter OpenAppend(){
		Directory.CreateDirectory(Path.GetDirectoryName(DataBinPath)??"");
		bool fresh=!File.Exists(DataBinPath)||new FileInfo(DataBinPath).Length==0;
		BinaryWriter w=new BinaryWriter(File.Open(DataBinPath,FileMode.Append));
		if(fresh){ w.Write(BinMagic); w.Write(BinVer); }// журнал создан дописыванием — заголовок на месте
		return w;
	}
	void AppendRec(byte type,byte[] payload){
		lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,type,payload); } catch{} }
	}
	public void AppendMeasurement(Measurement m){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecMeas,MeasPayload(m,PoolWriter(w))); } catch{} } }
	public void AppendEmotion(EmotionEntry e){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecEmo,EmoPayload(e,PoolWriter(w))); } catch{} } }
	public void AppendNoise(NoiseEntry n2){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecNoi,NoiPayload(n2,PoolWriter(w))); } catch{} } }
	public void AppendSleep(SleepEntry s2){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecSleep,SleepPayload(s2,PoolWriter(w))); } catch{} } }
	public void AppendMedTake(MedTakeEntry mt){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecMedTake,MedTakePayload(mt,PoolWriter(w))); } catch{} } }
	public void AppendGuidRec(byte type,string id){ Guid g; if(Guid.TryParse(id,out g)) AppendRec(type,RecPayload(w=>w.Write(g.ToByteArray()))); }
	void AppendPopup(){ if(D.LastPopup!=null) AppendRec(RecPopup,RecPayload(w=>w.Write(D.LastPopup.Value.Ticks))); }
	bool SameId(string id,Guid g){ Guid g2; return Guid.TryParse(id,out g2)&&g2==g; }
	string GetPool(Dictionary<int,string> pool,int id){ string s3; return pool.TryGetValue(id,out s3)?s3:""; }
	bool IsBpmn(string path){
		try{ using(BinaryReader r=new BinaryReader(File.OpenRead(path))) return r.ReadUInt32()==BinMagic; } catch{ return false; }
	}
	void LoadBin(){
		_pool.Clear(); _poolNext=0; _logRecords=0;
		Dictionary<int,string> pool=new Dictionary<int,string>();
		byte[] all=File.ReadAllBytes(DataBinPath);
		using(MemoryStream ms=new MemoryStream(all))
		using(BinaryReader r=new BinaryReader(ms)){
			if(r.ReadUInt32()!=BinMagic) throw new Exception("bad data.bin magic");
			if(r.ReadByte()!=BinVer) throw new Exception("bad data.bin version");
			try{
				while(r.BaseStream.Position<r.BaseStream.Length){
					byte type=r.ReadByte(); int len=r.ReadInt32();
					if(len<0||r.BaseStream.Position+len>r.BaseStream.Length) break;// оборванный хвост после краша
					byte[] pl=r.ReadBytes(len); _logRecords++;
					using(BinaryReader p=new BinaryReader(new MemoryStream(pl))){
						if(type==RecStr){ int id=p.ReadUInt16(); string sv=p.ReadString(); pool[id]=sv; if(id>=_poolNext) _poolNext=id+1; }
						else if(type==RecMeas){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							int sys=p.ReadUInt16(),dia=p.ReadUInt16(),pulse=p.ReadUInt16();
							byte fl=p.ReadByte();
							float? press=(fl&1)!=0?(float?)p.ReadSingle():null;
							float? temp=(fl&2)!=0?(float?)p.ReadSingle():null;
							float? hum=(fl&4)!=0?(float?)p.ReadSingle():null;
							int? kp=(fl&8)!=0?(int?)p.ReadInt16():null;
							float? emo=(fl&16)!=0?(float?)p.ReadSByte():null;
							float? sleepH=(fl&32)!=0?(float?)p.ReadSingle():null;
							int? sleepQ=(fl&64)!=0?(int?)p.ReadByte():null;
							int wxId=p.ReadUInt16();
							int mc=p.ReadByte(); List<string> meds=new List<string>();
							for(int i2=0;i2<mc;i2++) meds.Add(GetPool(pool,p.ReadUInt16()));
							int nc=p.ReadByte(); List<string> noise=new List<string>();
							for(int i2=0;i2<nc;i2++) noise.Add(GetPool(pool,p.ReadUInt16()));
							string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.M.RemoveAll(x=>SameId(x.Id,g));
							D.M.Add(new Measurement{
								Id=g.ToString("N"),T=t,Sys=sys,Dia=dia,Pulse=pulse,
								PressMm=press,TempC=temp,Hum=hum,Kp=kp,SleepH=sleepH,SleepQ=sleepQ,
								EmotionV=emo,EmotionLabel=emo!=null?DataStore.EmotionLabel((int)emo.Value):"",
								Wx=wxId==0xFFFF?"":GetPool(pool,wxId),Meds=meds,Noise=noise,Note=note});
						}
						else if(type==RecMeasDel){ Guid g=new Guid(p.ReadBytes(16)); D.M.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecEmo){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							int val=p.ReadSByte(); string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.E.RemoveAll(x=>SameId(x.Id,g));
							D.E.Add(new EmotionEntry{Id=g.ToString("N"),T=t,Val=val,Note=note});
						}
						else if(type==RecEmoDel){ Guid g=new Guid(p.ReadBytes(16)); D.E.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecNoi){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							string tp=GetPool(pool,p.ReadUInt16()); string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.N.RemoveAll(x=>SameId(x.Id,g));
							D.N.Add(new NoiseEntry{Id=g.ToString("N"),T=t,Type=tp,Note=note});
						}
						else if(type==RecNoiDel){ Guid g=new Guid(p.ReadBytes(16)); D.N.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecSleep){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							float hours=p.ReadSingle(); int q=p.ReadByte(); string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.S.RemoveAll(x=>SameId(x.Id,g));
							D.S.Add(new SleepEntry{Id=g.ToString("N"),T=t,Hours=hours,Quality=q,Note=note});
						}
						else if(type==RecSleepDel){ Guid g=new Guid(p.ReadBytes(16)); D.S.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecMedTake){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							string med=GetPool(pool,p.ReadUInt16());
							D.MedTakes.RemoveAll(x=>SameId(x.Id,g));
							D.MedTakes.Add(new MedTakeEntry{Id=g.ToString("N"),T=t,Med=med});
						}
						else if(type==RecMedTakeDel){ Guid g=new Guid(p.ReadBytes(16)); D.MedTakes.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecPopup) D.LastPopup=new DateTime(p.ReadInt64());
						// неизвестный тип — пропущен (задел на будущее)
					}
				}
			} catch(Exception){// битая/оборванная запись — останавливаемся, что прочитано, то прочитано
				if(_logRecords==0) throw;// файл совсем битой — пусть уходит в карантин
			}
		}
		foreach(KeyValuePair<int,string> kv in pool) _pool[kv.Value]=kv.Key;
	}
	bool LoadNn(){
		if(!File.Exists(NnPath)) return false;
		try{
			using(BinaryReader r=new BinaryReader(File.OpenRead(NnPath))){
				if(r.ReadUInt32()!=BinMagic||r.ReadByte()!=BinVer) return false;
				if(r.ReadByte()!=3) return false;// старый однослойный формат -> пересоздать
				int h=r.ReadInt32(),f=r.ReadInt32(),h2=r.ReadInt32();
				NnModel n=new NnModel{ W1=new double[h,f],B1=new double[h],W1b=new double[h2,h],B1b=new double[h2],W2=new double[2,h2],B2=new double[2] };
				for(int i=0;i<h;i++) for(int j=0;j<f;j++) n.W1[i,j]=r.ReadDouble();
				for(int i=0;i<h;i++) n.B1[i]=r.ReadDouble();
				for(int i=0;i<h2;i++) for(int j=0;j<h;j++) n.W1b[i,j]=r.ReadDouble();
				for(int i=0;i<h2;i++) n.B1b[i]=r.ReadDouble();
				for(int o=0;o<2;o++) for(int i=0;i<h2;i++) n.W2[o,i]=r.ReadDouble();
				for(int o=0;o<2;o++) n.B2[o]=r.ReadDouble();
				int lc=r.ReadInt32();
				for(int i=0;i<lc;i++){ long ticks=r.ReadInt64(); double l=r.ReadDouble(); n.Loss.Add(new LossPt{ T=new DateTime(ticks).ToString("MM-dd HH:mm"),L=l }); }
				D.Nn=n;
				return true;
			}
		} catch{ return false; }
	}
}
}
