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
// НН: фичи, SGD, GA, holdout-валидация, тюн; ФК-справочник (порт BpMonitor.cs)
static partial class App
{
	// ----------------------------- НН: инициализация -----------------------------
	public static NnModel NewNn(int hidden,int hidden2,int f){
		Random rnd=new Random(42);
		double[,] w1=new double[hidden,f];
		double[] b1=new double[hidden];
		double[,] w1b=new double[hidden2,hidden];
		double[] b1b=new double[hidden2];
		double[,] w2=new double[2,hidden2];
		double[] b2=new double[2];
		for(int i=0;i<hidden;i++)
		for(int j=0;j<f;j++)
			w1[i,j]=(rnd.NextDouble()*2-1)*0.4;
		for(int i=0;i<hidden2;i++)
		for(int j=0;j<hidden;j++)
			w1b[i,j]=(rnd.NextDouble()*2-1)*0.4;
		for(int i=0;i<2;i++)
		for(int j=0;j<hidden2;j++)
			w2[i,j]=(rnd.NextDouble()*2-1)*0.4;
		return new NnModel{W1=w1,B1=b1,W1b=w1b,B1b=b1b,W2=w2,B2=b2};
	}

	// ----------------------------- НН: фичи -----------------------------
	// контекст для НН: запись засчитывается, если сделана не раньше окна и не позже 10 мин ПОСЛЕ измерения
	// (обычный workflow: померил — потом сразу записал эмоцию/шум/сон). Нет записи или нулевое значение -> нейтраль
	static float EmotionAt(DateTime t){
		EmotionEntry e=D.E.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(12)).OrderByDescending(x=>x.T).FirstOrDefault();
		return e?.Val??0f;// шкала -10..+10, 0 = нейтрально
	}
	static int NoiseAt(DateTime t){
		int ns=D.N.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(3)).Select(x=>x.Type).Distinct().Count();
		return ns;// нет записей = 0 = тихо, это и есть нейтраль
	}
	static float SleepAt(DateTime t){
		SleepEntry s=D.S.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(36)).OrderByDescending(x=>x.T).FirstOrDefault();
		return s!=null&&s.Hours>0?s.Hours:7.5f;// нет/ноль -> нейтральные 7.5 ч
	}
	// качество сна 0..10 (шкала журнала), нет записи -> нейтральные 5
	static float SleepQualityAt(DateTime t){
		SleepEntry s=D.S.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(36)).OrderByDescending(x=>x.T).FirstOrDefault();
		return s!=null?s.Quality:5f;
	}
	// ----------------------------- фармакокинетика лекарств -----------------------------
	// T½ / длительность действия, часы. Источники: StatPearls NCBI NBK554579, DailyMed,
	// PMC8109523 (АКК), SmPC бисопролола, Br J Clin Pharmacol (метотрексат низкие дозы).
	// Значения по умолчанию для неизвестных: 8/12 ч — правятся руками в UI.
	// Встроенный эталон (StatPearls/DailyMed и др.) — пишется в medpk.json при первом запуске.
	static readonly Dictionary<string,double[]> MedPkDefault=new Dictionary<string,double[]>{
		{"каптоприл",new[]{2.0,8.0}},{"эналаприл",new[]{11.0,24.0}},{"лизиноприл",new[]{12.0,24.0}},
		{"рамиприл",new[]{15.0,24.0}},{"периндоприл",new[]{24.0,24.0}},
		{"амлодипин",new[]{40.0,36.0}},{"нифедипин",new[]{2.0,8.0}},{"фелодипин",new[]{14.0,24.0}},
		{"дилтиазем",new[]{4.0,12.0}},{"верапамил",new[]{7.0,12.0}},
		{"лозартан",new[]{6.0,24.0}},{"валсартан",new[]{7.0,24.0}},{"телмисартан",new[]{24.0,24.0}},
		{"кандесартан",new[]{9.0,24.0}},{"ирбесартан",new[]{13.0,24.0}},{"олмесартан",new[]{13.0,24.0}},
		{"бисопролол",new[]{10.0,24.0}},{"метопролол",new[]{4.0,12.0}},{"атенолол",new[]{7.0,24.0}},
		{"небиволол",new[]{2.0,24.0}},{"небилет",new[]{2.0,24.0}},{"каведилол",new[]{8.0,24.0}},
		{"гидрохлоротиазид",new[]{10.0,12.0}},{"хлорталидон",new[]{45.0,60.0}},{"индапамид",new[]{16.0,24.0}},
		{"спиронолактон",new[]{18.0,24.0}},{"верошпирон",new[]{18.0,24.0}},
		{"фуросемид",new[]{2.0,6.0}},{"торасемид",new[]{4.0,8.0}},
		{"моксонидин",new[]{3.0,8.0}},{"физиотенз",new[]{3.0,8.0}},{"клофелин",new[]{14.0,8.0}},
		{"метилдопа",new[]{2.0,12.0}},{"метотрексат",new[]{7.0,24.0}},{"фолиевая",new[]{6.0,24.0}},
		{"ибупрофен",new[]{2.0,6.0}},{"диклофенак",new[]{2.0,6.0}},{"преднизолон",new[]{3.0,24.0}},
		// торговые марки (РФ)
		{"энап",new[]{11.0,24.0}},{"диротон",new[]{15.0,24.0}},{"престариум",new[]{24.0,24.0}},{"нолипрел",new[]{24.0,24.0}},
		{"лозап",new[]{6.0,24.0}},{"вальсакор",new[]{7.0,24.0}},{"микорам",new[]{24.0,24.0}},
		{"эгилок",new[]{4.0,12.0}},{"вазокардин",new[]{4.0,12.0}},{"корвитол",new[]{4.0,12.0}},
		{"капозид",new[]{2.0,8.0}},{"телинстар",new[]{24.0,24.0}},{"ко-диован",new[]{7.0,24.0}},
		{"крестор",new[]{19.0,24.0}},{"розувастатин",new[]{19.0,24.0}},{"аторвастатин",new[]{14.0,24.0}}
	};
	// Рабочий справочник: грузится из medpk.json рядом с exe (правится без пересборки —
	// руками или во вкладке «Лекарства»); при отсутствии/битости файла создаётся из эталона.
	public static Dictionary<string,double[]> MedPkDb=new Dictionary<string,double[]>(MedPkDefault);
	static string MedPkPath{ get{ return Path.Combine(ExeDir,"medpk.json"); } }
	public static void LoadMedPk(){
		try{
			if(File.Exists(MedPkPath)){
				JObject j=JObject.Parse(File.ReadAllText(MedPkPath,Encoding.UTF8));
				Dictionary<string,double[]> nd=new Dictionary<string,double[]>();
				foreach(KeyValuePair<string,JToken> kv in j){
					JArray a=kv.Value as JArray; if(a==null||a.Count<2) continue;
					string k=NormMed(kv.Key); if(k=="") continue;
					double h=(double?)a[0]??0,d=(double?)a[1]??0;
					if(h>0&&d>0) nd[k]=new[]{h,d};
				}
				if(nd.Count>0){ MedPkDb=nd; return; }
			}
		} catch{ Quarantine(MedPkPath); }// битый справочник спасён от перезаписи, ставим эталон
		MedPkDb=new Dictionary<string,double[]>(MedPkDefault);
		SaveMedPk();
	}
	public static void SaveMedPk(){
		lock(Io){ try{ File.WriteAllText(MedPkPath,JsonConvert.SerializeObject(MedPkDb,Formatting.Indented)); } catch{} }
	}
	public static string NormMed(string s){
		return new string((s??"").ToLowerInvariant().Replace('ё','е').Where(char.IsLetterOrDigit).ToArray());
	}
	// действующая PK: пользовательский override -> справочник -> 8/12 ч
	public static (double half,double dur) MedKtFor(string name){
		string n=NormMed(name);
		if(Cfg.MedKt!=null) foreach(KeyValuePair<string,MedKt> kv in Cfg.MedKt)
			if(kv.Value!=null&&NormMed(kv.Key)==n) return (Math.Max(0.25,kv.Value.HalfH),Math.Max(0.5,kv.Value.DurH));
		foreach(KeyValuePair<string,double[]> kv in MedPkDb)
			if(n==kv.Key||n.Contains(kv.Key)) return (kv.Value[0],kv.Value[1]);
		return (8.0,12.0);
	}
	// времена приёма каждого лекарства: отметки приёма (журнал «Лекарства») + лекарства,
	// отмеченные на измерениях. Фичи НН строятся на фактических приёмах, а не только на ручном вводе
	static Dictionary<string,List<DateTime>> MedIntakes(){
		Dictionary<string,List<DateTime>> d=new Dictionary<string,List<DateTime>>();
		Action<string,DateTime> add=(s,tt)=>{
			string k=NormMed(s); if(k=="") return;
			List<DateTime> l;
			if(!d.TryGetValue(k,out l)){l=new List<DateTime>();d[k]=l;}
			l.Add(tt);
		};
		foreach(Measurement m in D.M) foreach(string s in m.Meds) add(s,m.T);
		foreach(MedTakeEntry mt in D.MedTakes) add(mt.Med,mt.T);
		return d;
	}
	// размерность входа зависит от списка лекарств; при изменении — переучиваем модель с нуля
	public static void EnsureNnDim(){
		if(D.Nn?.W1!=null&&D.Nn.W1b!=null&&D.Nn.W1.GetLength(1)==InDim()&&D.Nn.W1.GetLength(0)==Cfg.Nn.Hidden&&D.Nn.W1b.GetLength(0)==Cfg.Nn.Hidden2) return;		D.Nn=NewNn(Cfg.Nn.Hidden,Cfg.Nn.Hidden2,InDim());		Train();
		SaveNn();
	}

	public static double[] FeatsFor(Measurement m,Dictionary<string,List<DateTime>> intakes=null){
		if(intakes==null) intakes=MedIntakes();
		double h=m.T.Hour+m.T.Minute/60.0;
		double dow=((int)m.T.DayOfWeek+6)%7;
		double[] x=new double[InDim()];
		x[0]=Math.Sin(2*Math.PI*h/24); x[1]=Math.Cos(2*Math.PI*h/24);
		x[2]=Math.Sin(2*Math.PI*dow/7); x[3]=Math.Cos(2*Math.PI*dow/7);
		x[4]=((double)(m.PressMm??1013f)-1013)/40;
		x[5]=(double)(m.TempC??15f)/15;
		x[6]=(double)(m.Hum??60f)/100;
		x[7]=(double)(m.Kp??33)/90;
		x[8]=EmotionAt(m.T)/10.0;
		x[9]=NoiseAt(m.T)/4.0;
		x[10]=SleepAt(m.T)/10.0;
		x[11]=(SleepQualityAt(m.T)-5.0)/5.0;// -1..+1 вокруг нейтрали 5
		x[12]=m.Meds.Count/4.0;
		for(int k=0;k<Cfg.Meds.Count;k++){// PK-фичи: остаточная активность и нахождение в окне действия
			(double half,double dur)=MedKtFor(Cfg.Meds[k]);
			double conc=0,win=0;
			List<DateTime> ts;
			if(intakes.TryGetValue(NormMed(Cfg.Meds[k]),out ts)) foreach(DateTime ti in ts){
				if(ti>m.T) continue;
				double dt=(m.T-ti).TotalHours;
				conc+=Math.Pow(0.5,dt/half);// элиминация 1-го порядка: "остаток дозы"
				if(dt<=dur){double w=1-dt/dur;if(w>win)win=w;}
			}
			x[FeatBase.Length+2*k]=Math.Min(conc,3.0)/3.0;
			x[FeatBase.Length+2*k+1]=win;
		}
		return x;
	}
	static double[] Forward(NnModel n,double[] x){
		int h1=n.W1.GetLength(0),h2=n.W1b.GetLength(0);
		double[] a1=new double[h1];
		for(int i=0;i<h1;i++){
			double s=n.B1[i];
			for(int j=0;j<x.Length;j++) s+=n.W1[i,j]*x[j];
			a1[i]=Math.Tanh(s);
		}
		double[] a2=new double[h2];
		for(int i=0;i<h2;i++){
			double s=n.B1b[i];
			for(int j=0;j<h1;j++) s+=n.W1b[i,j]*a1[j];
			a2[i]=Math.Tanh(s);
		}
		double[] y=new double[2];
		for(int o=0;o<2;o++){
			double s=n.B2[o];
			for(int i=0;i<h2;i++) s+=n.W2[o,i]*a2[i];
			y[o]=s;
		}
		return y;
	}
	public static(double sys,double dia) PredictNow(){
		Measurement m=new Measurement{
			T=DateTime.Now,
			PressMm=Weather.PressMm,TempC=Weather.TempC,Hum=Weather.Hum,Kp=Weather.Kp
		};
		double[] y=Forward(D.Nn,FeatsFor(m));
		double sys=Clamp(120+30*y[0],70,250),dia=Clamp(80+20*y[1],40,150);
		if(dia>=sys) dia=sys-20;
		return(sys,dia);
	}

	// Обучает НН по всем записям (online: вызывается при каждом вводе)
	public static void Train(){
		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		List<double[]> xs=new List<double[]>();
		List<double[]> ys=new List<double[]>();
		foreach(Measurement m in D.M.Where(m=>m.Sys>0).OrderByDescending(m=>m.T).Take(2000)){
			xs.Add(FeatsFor(m,intakes));
			ys.Add(new[]{(m.Sys-120.0)/30.0,(m.Dia-80.0)/20.0});
		}
		if(xs.Count<2) return;
		TrainModel(D.Nn,xs,ys,Cfg.Nn.Lr,Math.Max(1,Cfg.Nn.Epochs),true);
	}
	// SGD по готовой выборке; recordLoss — записать loss последнего эпоха в n.Loss;
	// seed != 0 — детерминированный перемешивающий генератор (для самотеста валидации)
	public static void TrainModel(NnModel n,List<double[]> xs,List<double[]> ys,double lr,int epochs,bool recordLoss,int seed=0){
		int hidden=n.W1.GetLength(0),f=n.W1.GetLength(1),h2=n.W1b.GetLength(0);
		Random rnd=seed!=0?new Random(seed):new Random();
		for(int ep=0;ep<epochs;ep++){
			for(int i=xs.Count-1;i>0;i--){
				int j=rnd.Next(i+1);
				(xs[i],xs[j])=(xs[j],xs[i]);
				(ys[i],ys[j])=(ys[j],ys[i]);
			}
			double sum=0;
			for(int s=0;s<xs.Count;s++){
				double[] x=xs[s];
				double[] y=ys[s];
				double[] a1=new double[hidden];
				double[] d1=new double[hidden];
				for(int i=0;i<hidden;i++){
					double t=n.B1[i];
					for(int k2=0;k2<f;k2++) t+=n.W1[i,k2]*x[k2];
					a1[i]=Math.Tanh(t);
					d1[i]=a1[i]*(1-a1[i]*a1[i]);
				}
				double[] a2=new double[h2];
				double[] d2=new double[h2];
				for(int i=0;i<h2;i++){
					double t=n.B1b[i];
					for(int j2=0;j2<hidden;j2++) t+=n.W1b[i,j2]*a1[j2];
					a2[i]=Math.Tanh(t);
					d2[i]=a2[i]*(1-a2[i]*a2[i]);
				}
				double[] err=new double[2];
				for(int o=0;o<2;o++){
					double pr=n.B2[o];
					for(int i=0;i<h2;i++) pr+=n.W2[o,i]*a2[i];
					err[o]=pr-y[o];
					sum+=err[o]*err[o];
				}
				// backprop по СТАРЫМ весам: слой2 -> слой1
				double[] dh2=new double[h2];
				for(int i=0;i<h2;i++){
					double g3=0;
					for(int o=0;o<2;o++) g3+=n.W2[o,i]*err[o];
					dh2[i]=d2[i]*g3;
				}
				double[] dh1=new double[hidden];
				for(int j2=0;j2<hidden;j2++){
					double g2=0;
					for(int i=0;i<h2;i++) g2+=n.W1b[i,j2]*dh2[i];
					dh1[j2]=d1[j2]*g2;
				}
				for(int o=0;o<2;o++)
				for(int i=0;i<h2;i++)
					n.W2[o,i]-=err[o]*a2[i]*lr;
				for(int o=0;o<2;o++) n.B2[o]-=err[o]*lr;
				for(int i=0;i<h2;i++)
				for(int j2=0;j2<hidden;j2++)
					n.W1b[i,j2]-=dh2[i]*a1[j2]*lr;
				for(int i=0;i<h2;i++) n.B1b[i]-=dh2[i]*lr;
				for(int i=0;i<hidden;i++){
					double g=dh1[i]*lr;
					for(int k2=0;k2<f;k2++) n.W1[i,k2]-=g*x[k2];
					n.B1[i]-=g;
				}
			}
			if(!recordLoss||ep!=epochs-1) continue;
			n.Loss.Add(new LossPt{T=DateTime.Now.ToString("MM-dd HH:mm"),L=Math.Round(sum/(xs.Count*2.0),4)});
			if(n.Loss.Count>500) n.Loss.RemoveRange(0,n.Loss.Count-500);
		}
	}

	// ----------------------------- НН: генетическое обучение -----------------------------
	static double[] Pack(NnModel n){
		List<double> v=new List<double>();
		foreach(double d in n.W1) v.Add(d); foreach(double d in n.B1) v.Add(d);
		foreach(double d in n.W1b) v.Add(d); foreach(double d in n.B1b) v.Add(d);
		foreach(double d in n.W2) v.Add(d); foreach(double d in n.B2) v.Add(d);
		return v.ToArray();
	}
	static void Unpack(NnModel n,double[] v){
		int q=0;
		for(int i=0;i<n.W1.GetLength(0);i++) for(int j=0;j<n.W1.GetLength(1);j++) n.W1[i,j]=v[q++];
		for(int i=0;i<n.B1.Length;i++) n.B1[i]=v[q++];
		for(int i=0;i<n.W1b.GetLength(0);i++) for(int j=0;j<n.W1b.GetLength(1);j++) n.W1b[i,j]=v[q++];
		for(int i=0;i<n.B1b.Length;i++) n.B1b[i]=v[q++];
		for(int i=0;i<n.W2.GetLength(0);i++) for(int j=0;j<n.W2.GetLength(1);j++) n.W2[i,j]=v[q++];
		for(int i=0;i<n.B2.Length;i++) n.B2[i]=v[q++];
	}
	static double GaFit(double[] v,NnModel tmp,List<double[]> xs,List<double[]> ys){
		Unpack(tmp,v);
		double sum=0;
		for(int s=0;s<xs.Count;s++){
			double[] y=Forward(tmp,xs[s]);
			double e0=y[0]-ys[s][0],e1=y[1]-ys[s][1];
			sum+=e0*e0+e1*e1;
		}
		return sum/(xs.Count*2.0);
	}
	// GA: популяция весов, турнирный отбор, элитизм, гауссова мутация с затуханием sigma
	// seed != 0 — детерминированный запуск (для тестов/повторяемости)
	public static double TrainGenetic(int popSize=60,int generations=250,int seed=0){
		NnModel n=D.Nn;
		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		List<double[]> xs=new List<double[]>();
		List<double[]> ys=new List<double[]>();
		foreach(Measurement m in D.M.Where(m=>m.Sys>0).OrderByDescending(m=>m.T).Take(2000)){
			xs.Add(FeatsFor(m,intakes));
			ys.Add(new[]{(m.Sys-120.0)/30.0,(m.Dia-80.0)/20.0});
		}
		if(xs.Count<2) return 0;
		int dim=Pack(n).Length;
		NnModel tmp=new NnModel{W1=(double[,])n.W1.Clone(),B1=(double[])n.B1.Clone(),W1b=(double[,])n.W1b.Clone(),B1b=(double[])n.B1b.Clone(),W2=(double[,])n.W2.Clone(),B2=(double[])n.B2.Clone()};
		Random rnd=seed!=0?new Random(seed):new Random();
		double[][] pop=new double[popSize][];
		pop[0]=Pack(n);// элита-затравка: текущие веса
		for(int i=1;i<popSize;i++){
			double[] g=new double[dim];
			for(int k=0;k<dim;k++) g[k]=(rnd.NextDouble()*2-1)*0.5;
			pop[i]=g;
		}
		double best=double.MaxValue; double[] bestV=null;
		double sigma=0.25;
		for(int gen=0;gen<generations;gen++){
			double[] fit=new double[popSize];
			for(int i=0;i<popSize;i++) fit[i]=GaFit(pop[i],tmp,xs,ys);
			Array.Sort(fit,pop);
			if(fit[0]<best){best=fit[0];bestV=(double[])pop[0].Clone();}
			double[][] next=new double[popSize][];
			next[0]=pop[0]; next[1]=pop[1];// элитизм
			for(int i=2;i<popSize;i++){
				double[] a=pop[Tournament(fit,rnd)],b=pop[Tournament(fit,rnd)];
				double[] c=(double[])a.Clone();
				for(int k=0;k<dim;k++) if(rnd.NextDouble()<0.5) c[k]=b[k];// равномерный кроссовер
				for(int k=0;k<dim;k++) c[k]+=(rnd.NextDouble()*2-1)*sigma;// мутация
				next[i]=c;
			}
			pop=next;
			sigma=Math.Max(0.01,sigma*0.985);
		}
		if(bestV!=null){
			Unpack(n,bestV);
			n.Loss.Add(new LossPt{T=DateTime.Now.ToString("MM-dd HH:mm"),L=Math.Round(best,4)});
			if(n.Loss.Count>500) n.Loss.RemoveRange(0,n.Loss.Count-500);
		}
		return best;
	}
	static int Tournament(double[] fit,Random rnd){
		int a=rnd.Next(fit.Length),b=rnd.Next(fit.Length),c=rnd.Next(fit.Length);
		int best=fit[a]<=fit[b]?a:b;
		return fit[c]<fit[best]?c:best;
	}

	// ----------------------------- НН: holdout-валидация + значимость фич -----------------------------
	// Обучение loss скрывает переобученность: модель видит все записи. Здесь же НН обучается
	// с нуля на всём, КРОМЕ последних N записей (рабочая модель D.Nn не трогается), и проверяется
	// на удержанных: MAE/RMSE в мм рт.ст. Значимость фич — перестановочная: колонка фичи на
	// holdout перемешивается, насколько вырос MSE — столько фича и двигает прогноз.
	// Результат живёт в data\validate.json и отдаётся в /api/state (nn.validate).
	static JObject _lastValidation;
	public static JObject LastValidation=>_lastValidation;
	public static void LoadValidation(){
		try{
			string f=Path.Combine(ExeDir,"data","validate.json");
			if(File.Exists(f)) _lastValidation=JObject.Parse(File.ReadAllText(f,Encoding.UTF8));
		} catch(Exception ex){ Log("validate.json: "+ex.Message); }
	}
	public static void SetValidation(JObject r){ _lastValidation=r; SaveValidation(); }
	static void SaveValidation(){
		try{
			if(_lastValidation!=null) File.WriteAllText(Path.Combine(ExeDir,"data","validate.json"),
				_lastValidation.ToString(Newtonsoft.Json.Formatting.None),Encoding.UTF8);
		} catch(Exception ex){ Log("validate.json: "+ex.Message); }
	}
	static double Mse(NnModel n,List<double[]> xs,List<double[]> ys){
		double sum=0;
		for(int s=0;s<xs.Count;s++){
			double[] y=Forward(n,xs[s]);
			double e0=y[0]-ys[s][0],e1=y[1]-ys[s][1];
			sum+=e0*e0+e1*e1;
		}
		return sum/(xs.Count*2.0);
	}
	// holdoutN<=0 — авто: max(5, min(40, n/5)); seed — детерминированность перестановок
	public static JObject Validate(int holdoutN=0,int seed=42){
		List<Measurement> items=D.M.Where(m=>m.Sys>0).OrderBy(m=>m.T).ToList();
		int n=items.Count;
		if(n<20) throw new Exception("мало данных для валидации: "+n+" записей (нужно ≥20)");
		int hold=holdoutN>0?Math.Min(holdoutN,n-10):Math.Max(5,Math.Min(40,n/5));
		if(hold<5) hold=5;
		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		List<double[]> xs=new List<double[]>(),xh=new List<double[]>(),ys=new List<double[]>(),yh=new List<double[]>();
		for(int i=0;i<n;i++){
			double[] x=FeatsFor(items[i],intakes);
			double[] y=new[]{(items[i].Sys-120.0)/30.0,(items[i].Dia-80.0)/20.0};
			if(i<n-hold){ xs.Add(x); ys.Add(y); } else { xh.Add(x); yh.Add(y); }
		}
		JobProgress(1,3);
		NnModel tmp=NewNn(Cfg.Nn.Hidden,Cfg.Nn.Hidden2,InDim());// свежая модель без последних N записей
		TrainModel(tmp,xs,ys,Cfg.Nn.Lr,Math.Max(1,Cfg.Nn.Epochs),false,seed);
		JobProgress(2,3);
		double maeS=0,maeD=0,sqS=0,sqD=0;
		for(int i=0;i<xh.Count;i++){
			double[] y=Forward(tmp,xh[i]);
			double ps=Clamp(120+30*y[0],70,250),pd=Clamp(80+20*y[1],40,150);
			if(pd>=ps) pd=ps-20;
			double es=ps-items[n-hold+i].Sys,ed=pd-items[n-hold+i].Dia;
			maeS+=Math.Abs(es); maeD+=Math.Abs(ed); sqS+=es*es; sqD+=ed*ed;
		}
		double baseMse=Mse(tmp,xh,yh);
		string[] names=GetFeatNames();
		int f=xh[0].Length;
		List<JObject> imp=new List<JObject>();
		Random rnd=new Random(seed);
		const int reps=5;// усреднение перестановок: на маленьком holdout одиночный shuffle слишком шумный
		for(int j=0;j<f;j++){
			double acc=0;
			for(int r=0;r<reps;r++){
				List<double[]> sh=new List<double[]>();
				foreach(double[] x in xh) sh.Add((double[])x.Clone());
				for(int i=sh.Count-1;i>0;i--){ int k=rnd.Next(i+1); double t=sh[i][j]; sh[i][j]=sh[k][j]; sh[k][j]=t; }
				acc+=Mse(tmp,sh,yh);
			}
			imp.Add(new JObject{["name"]=names[j],["d"]=Math.Round(acc/reps-baseMse,4)});
		}
		JArray impArr=new JArray(imp.OrderByDescending(o=>(double)o["d"]).Cast<JToken>().ToList());
		JobProgress(3,3);
		return new JObject{
			["at"]=DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
			["n"]=n,["nTrain"]=xs.Count,["nHold"]=xh.Count,
			["maeSys"]=Math.Round(maeS/xh.Count,1),["maeDia"]=Math.Round(maeD/xh.Count,1),
			["rmseSys"]=Math.Round(Math.Sqrt(sqS/xh.Count),1),["rmseDia"]=Math.Round(Math.Sqrt(sqD/xh.Count),1),
			["trainMse"]=Math.Round(Mse(tmp,xs,ys),4),["holdMse"]=Math.Round(baseMse,4),
			["importance"]=impArr
		};
	}

	// ----------------------------- НН: автоподбор гиперпараметров -----------------------------
	// Сетка размеров слоёв прогоняется на том же holdout-разделе, что и Validate: каждая
	// конфигурация обучается с нуля на всех записях, кроме последних N, и сравнивается по
	// holdout MSE. Лучшая применяется в Cfg.Nn (модель пересоздаётся и переобучивается).
	static JObject _lastTune;
	public static JObject LastTune=>_lastTune;
	public static void LoadTune(){
		try{
			string f=Path.Combine(ExeDir,"data","tune.json");
			if(File.Exists(f)) _lastTune=JObject.Parse(File.ReadAllText(f,Encoding.UTF8));
		} catch(Exception ex){ Log("tune.json: "+ex.Message); }
	}
	public static void SetTune(JObject r){ _lastTune=r; SaveTune(); }
	static void SaveTune(){
		try{
			if(_lastTune!=null) File.WriteAllText(Path.Combine(ExeDir,"data","tune.json"),
				_lastTune.ToString(Newtonsoft.Json.Formatting.None),Encoding.UTF8);
		} catch(Exception ex){ Log("tune.json: "+ex.Message); }
	}
	public static JObject Tune(int holdoutN=0,int seed=42){
		List<Measurement> items=D.M.Where(m=>m.Sys>0).OrderBy(m=>m.T).ToList();
		int n=items.Count;
		if(n<20) throw new Exception("мало данных для подбора: "+n+" записей (нужно ≥20)");
		int hold=holdoutN>0?Math.Min(holdoutN,n-10):Math.Max(5,Math.Min(40,n/5));
		if(hold<5) hold=5;
		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		List<double[]> xs=new List<double[]>(),xh=new List<double[]>(),ys=new List<double[]>(),yh=new List<double[]>();
		for(int i=0;i<n;i++){
			double[] x=FeatsFor(items[i],intakes);
			double[] y=new[]{(items[i].Sys-120.0)/30.0,(items[i].Dia-80.0)/20.0};
			if(i<n-hold){ xs.Add(x); ys.Add(y); } else { xh.Add(x); yh.Add(y); }
		}
		int[] grid1={16,28,40},grid2={10,16,24};
		JArray results=new JArray();
		double bestMse=double.MaxValue; int bestH=Cfg.Nn.Hidden,bestH2=Cfg.Nn.Hidden2;
		int total=grid1.Length*grid2.Length,done=0;
		JobProgress(0,total);
		foreach(int h in grid1) foreach(int h2 in grid2){
			NnModel tmp=NewNn(h,h2,InDim());
			TrainModel(tmp,xs,ys,Cfg.Nn.Lr,Math.Max(1,Cfg.Nn.Epochs),false,seed);
			double hm=Mse(tmp,xh,yh);
			results.Add(new JObject{["hidden"]=h,["hidden2"]=h2,["holdMse"]=Math.Round(hm,5)});
			if(hm<bestMse){ bestMse=hm; bestH=h; bestH2=h2; }
			done++; JobProgress(done);
		}
		bool changed=bestH!=Cfg.Nn.Hidden||bestH2!=Cfg.Nn.Hidden2;
		Cfg.Nn.Hidden=bestH; Cfg.Nn.Hidden2=bestH2;
		SaveCfg();
		EnsureNnDim();// при смене размеров — пересоздание и полное переобучение рабочей модели
		return new JObject{
			["at"]=DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
			["n"]=n,["nTrain"]=xs.Count,["nHold"]=xh.Count,
			["hidden"]=bestH,["hidden2"]=bestH2,["holdMse"]=Math.Round(bestMse,5),
			["changed"]=changed,
			["results"]=new JArray(results.Cast<JObject>().OrderBy(o=>(double)o["holdMse"]).Cast<JToken>().ToList())
		};
	}
}
}
