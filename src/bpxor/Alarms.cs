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
// Фаза 4: логика будильников и напоминаний о лекарствах переехала в AlarmScheduler.cs —
// там то же расписание (daily/days/everyN, догон 30 мин), но по всем юзерам, в таймзоне
// юзера и с persist LastFired. Profile.CheckAlarms/CheckMedReminders удалены за ненадобностью.
}
