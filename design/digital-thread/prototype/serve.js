const http=require('http'),fs=require('fs'),path=require('path');
const dir=__dirname;
http.createServer((req,res)=>{
  let p=req.url.split('?')[0];
  if(p==='/')p='/aerolink-digital-thread-directions.html';
  const f=path.join(dir,decodeURIComponent(p));
  fs.readFile(f,(e,d)=>{
    if(e){res.writeHead(404);res.end('nope');return;}
    res.writeHead(200,{'Content-Type':p.endsWith('.html')?'text/html; charset=utf-8':'application/octet-stream'});
    res.end(d);
  });
}).listen(8791,()=>console.log('serving on 8791'));
