// Watchdog: счётчик сбоев push/email; раз в WatchdogCheckMin минут шлёт сводку на WatchdogMail
// (нужен настроенный SmtpUser). Письмо не ушло — счётчики возвращаются, прогон повторится на следующем тике.
using System;
using System.Threading;
namespace BpMonitor{
static class Watchdog{
	public static int PushFail,MailFail;
	static string _lastPushErr="",_lastMailErr="";
	static DateTime _lastAlert=DateTime.MinValue;
	public static void PushFailInc(string err){ Interlocked.Increment(ref PushFail); _lastPushErr=err; }
	public static void MailFailInc(string err){ Interlocked.Increment(ref MailFail); _lastMailErr=err; }
	public static void Tick(){
		var ac=Program.ACfg;
		if(string.IsNullOrWhiteSpace(ac.WatchdogMail)||string.IsNullOrWhiteSpace(ac.SmtpUser)) return;
		int win=Math.Max(10,ac.WatchdogCheckMin);
		if((DateTime.Now-_lastAlert).TotalMinutes<win) return;
		int pf=Interlocked.Exchange(ref PushFail,0),mf=Interlocked.Exchange(ref MailFail,0);
		_lastAlert=DateTime.Now;
		if(pf+mf==0) return;
		string body="bpxor watchdog: сбоя за последние "+win+" мин: push="+pf+", email="+mf
			+(string.IsNullOrEmpty(_lastPushErr)?"":"\nпоследняя push-ошибка: "+_lastPushErr)
			+(string.IsNullOrEmpty(_lastMailErr)?"":"\nпоследняя email-ошибка: "+_lastMailErr);
		try{ SmtpSender.Send(ac,ac.WatchdogMail,"bpxor watchdog: "+pf+" push / "+mf+" email",body); }
		catch(Exception ex){
			Console.WriteLine("watchdog mail: "+ex.Message);
			Interlocked.Add(ref PushFail,pf); Interlocked.Add(ref MailFail,mf);
		}
	}
}}
