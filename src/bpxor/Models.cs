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
public class AlarmDef{
	public string Time="08:00";
	public string Label="";
	public bool Enabled=true;
	public string Mode="daily";// daily | days (по дням недели) | everyN (каждые N дней)
	public List<int> Days=new List<int>();// ISO: 1=пн..7=вс, используется при Mode=days
	public int EveryN=1;// используется при Mode=everyN
	public string Anchor="";// дата старта цикла everyN (yyyyMMdd); пусто -> фиксируется при первом срабатывании
}
public class MedKt{
	public double HalfH=8;// период полувыведения, ч
	public double DurH=12;// длительность действия, ч
}
public class MedTakeEntry{
	public string Id;
	public DateTime T;
	public string Med;
}
public class HaCfg{
	public string Url="";
	public string Token="";
	public Dictionary<string,string> Sensors=new Dictionary<string,string>
		{{"pressure",""},{"temp",""},{"humidity",""},{"kp",""}};
}
public class NnCfg{
	public double Lr=0.05;
	public int Hidden=12;
	public int Hidden2=16;// второй скрытый слой
	public int Epochs=30;
}
public class Config{
	public int Port=8090;
	public double Lat=55.75,Lon=37.62;
	public int PollMin=15;// период опроса погоды, мин
	public string AlarmFile="alarm.mp3";// mp3 рядом с exe
	public int AlarmAheadMin=5;// напоминание в трее до будильника
	public bool Autostart=true;
	public int NormSys=135,NormDia=85;// "норма": прогноз выше -> popup
	public string Dob="";// дата рождения пользователя yyyy-MM-dd (фича НН "возраст"); пусто = не указан
	public string Sex="";// пол: "m"|"f" (фича НН "пол"); пусто = не указан
	public int PopupCooldownMin=120;// пауза между popup
	public HaCfg Ha=new HaCfg();
	public NnCfg Nn=new NnCfg();
	public List<AlarmDef> Alarms=new List<AlarmDef>{new AlarmDef{Time="08:00",Label="утро"},new AlarmDef{Time="20:00",Label="вечер"}};
	public List<string> Meds=new List<string>();
	public Dictionary<string,MedKt> MedKt=new Dictionary<string,MedKt>();// PK-переопределения по названию (реестр нечувствителен к регистру)
	public Dictionary<string,List<string>> MedTimes=new Dictionary<string,List<string>>();// расписание напоминаний о приёме: норм.название -> ["08:00","20:00"]
	public List<string> Noises=new List<string>{
		"сосед с перфоратором","гонки стритрейсеров под окнами","пьяная вечеринка с танцами над головой",
		"громкая музыка у соседей","стройка / ремонт","лающая собака","салют / громкие взрывы","самолёт низко над домом","тихо / ничего"
	};
}
public class Measurement{
	public string Id;
	public DateTime T;
	public int Sys,Dia,Pulse;
	public string Note="";
	// погода на момент измерения (снимок)
	public float? PressMm,TempC,Hum;
	public int? Kp;
	public string Wx="";
	public List<string> Meds=new List<string>();
	// контекст (снимок на момент записи; для NN используется живой джойн)
	public float? EmotionV;
	public string EmotionLabel="";
	public List<string> Noise=new List<string>();
	public float? SleepH;
	public int? SleepQ;
}
public class EmotionEntry{
	public string Id;
	public DateTime T;
	public int Val;
	public string Note="";
}
public class NoiseEntry{
	public string Id;
	public DateTime T;
	public string Type;
	public string Note="";
}
public class SleepEntry{
	public string Id;
	public DateTime T;
	public float Hours;
	public int Quality;
	public string Note="";
}
public class LossPt{
	public string T;
	public double L;
}
public class NnModel{
	public double[,] W1;
	public double[,] W1b;// второй скрытый слой (h2 x h1)
	public double[] B1b;
	public double[] B1;
	public double[,] W2;
	public double[] B2;
	public List<LossPt> Loss=new List<LossPt>();
}
public class DataStore{
	public List<Measurement> M=new List<Measurement>();
	public List<EmotionEntry> E=new List<EmotionEntry>();
	public List<NoiseEntry> N=new List<NoiseEntry>();
	public List<SleepEntry> S=new List<SleepEntry>();
	public List<MedTakeEntry> MedTakes=new List<MedTakeEntry>();
	public NnModel Nn;
	public DateTime? LastPopup;
	public static string EmotionLabel(int v){
		if(v<=-10) return"глубокая горесть (смерть близкого)";
		if(v<=-9) return"отчаяние";
		if(v<=-8) return"сильный страх";
		if(v<=-7) return"гнев";
		if(v<=-6) return"тревога / напряжение";
		if(v<=-5) return"раздражение";
		if(v<=-4) return"досада, суета";
		if(v<=-3) return"лёгкая усталость";
		if(v<=-2) return"небольшая грусть";
		if(v<=-1) return"спокойствие с лёгким негативом";
		if(v==0) return"нейтрально";
		if(v<=1) return"лёгкое удовлетворение";
		if(v<=2) return"покой";
		if(v<=3) return"лёгкая радость";
		if(v<=4) return"хорошее настроение";
		if(v<=5) return"радость";
		if(v<=6) return"воодушевление";
		if(v<=7) return"эйфория";
		if(v<=8) return"восторг (важное событие)";
		return v<=9?"экстремальное счастье":"пиковая радость (рождение ребёнка)";
	}
}
}
