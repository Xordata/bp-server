// bpxor service worker (Фаза 4): push-уведомления + «Стоп» без открытия вкладки.
self.addEventListener("push",function(e){
  var d={};try{d=e.data?e.data.json():{};}catch(_){d={body:e.data?e.data.text():""};}
  var opts={
    body:d.body||"",
    tag:d.tag||"bpxor",
    renotify:true,
    vibrate:d.vibrate||[300,150,300,150,300],
    data:d,
    actions:[{action:"stop",title:"\u23f9 \u0421\u0442\u043e\u043f"}]
  };
  if(d.icon)opts.icon=d.icon;
  e.waitUntil(self.registration.showNotification(d.title||"BpMonitor",opts));
});
self.addEventListener("notificationclick",function(e){
  e.notification.close();
  if(e.action==="stop"){
    // гасим звонок на сервере; вкладка услышит об этом через /api/alarm/state
    e.waitUntil(fetch("/api/alarm/dismiss",{method:"POST",credentials:"same-origin",
      headers:{"Content-Type":"application/json"},body:"{}"}));
    return;
  }
  e.waitUntil(self.clients.matchAll({type:"window",includeUncontrolled:true}).then(function(cs){
    for(var i=0;i<cs.length;i++){cs[i].focus();cs[i].postMessage({type:"bpxor-open"});return;}
    return self.clients.openWindow("/");
  }));
});
