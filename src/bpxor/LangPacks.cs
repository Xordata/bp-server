using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace BpMonitor{
// Языковые пакеты: <DataRoot>/lang/<code>.json — плоский JSON «русский исходник -> перевод».
// Ключ = сам русский текст (gettext-стиль): SPA переводит DOM-тексты и tr() по нему,
// серверные тексты (будильники push/email, письма экспорта) переводит Tr(). Файлы правятся без пересборки.
static class LangPacks{
	class Pack{ public DateTime Mtime; public Dictionary<string,string> Map=new Dictionary<string,string>(); }
	static readonly Dictionary<string,Pack> _cache=new Dictionary<string,Pack>(StringComparer.OrdinalIgnoreCase);
	public static string Tr(string lang,string ru){ return Tr(lang,ru,null); }
	public static string Tr(string lang,string ru,string dataRoot){
		if(string.IsNullOrEmpty(lang)||lang=="ru"||string.IsNullOrEmpty(ru)) return ru;
		var map=Map(lang,dataRoot??Program.ACfg.DataRoot);
		if(map!=null&&map.TryGetValue(ru,out string t)&&!string.IsNullOrEmpty(t)) return t;
		return ru;
	}
	static Dictionary<string,string> Map(string code,string dataRoot){
		if(code==null||!System.Text.RegularExpressions.Regex.IsMatch(code,"^[a-z]{2}$")) return null;
		string f=Path.Combine(dataRoot??"","lang",code+".json");
		if(!File.Exists(f)) return null;
		lock(_cache){
			try{
				DateTime mt=File.GetLastWriteTimeUtc(f);
				if(_cache.TryGetValue(code,out Pack p)&&p.Mtime==mt) return p.Map;
				var map=new Dictionary<string,string>();
				JObject jo=JObject.Parse(File.ReadAllText(f));
				foreach(var kv in jo.Properties()) map[kv.Name]=(string)kv.Value??"";
				_cache[code]=new Pack{Mtime=mt,Map=map};
				return map;
			} catch { return null; }
		}
	}
}
}
