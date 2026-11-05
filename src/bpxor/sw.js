// bpxor service worker (Фаза 4 + F1 PWA): push + «Стоп» + кэш статики для установки на домашний экран.
var CACHE="bpxor-v1";
var STATIC=["/alarm.mp3","/manifest.webmanifest","/icons/icon-192.png","/icons/icon-512.png","/icons/icon-maskable-512.png","/icons/apple-touch-icon.png"];
self.addEventListener("install",function(e){
  e.waitUntil(caches.open(CACHE).then(function(c){return c.addAll(STATIC);}).catch(function(){}));
  self.skipWaiting();
});
self.addEventListener("activate",function(e){
  e.waitUntil(caches.keys().then(function(ks){return Promise.all(ks.filter(function(k){return k!==CACHE;}).map(function(k){return caches.delete(k);}));}));
  self.clients.claim();
});
// cache-first только для тяжёлой неизменяемой статики; остальное — всегда сеть (свежий HTML/API)
self.addEventListener("fetch",function(e){
  var u=e.request.url;
  if(STATIC.some(function(p){return u.indexOf(p)>=0;})){
    e.respondWith(caches.match(e.request).then(function(r){
      return r||fetch(e.request).then(function(resp){
        if(resp.ok){var cl=resp.clone();caches.open(CACHE).then(function(c){c.put(e.request,cl);});}
        return resp;
      });
    }));
  }
});
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
