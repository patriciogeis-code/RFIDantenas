const http = require('http');

const PROXY_PORT = 8091;
const TARGET_HOST = '127.0.0.1';
const TARGET_PORT = 8090;

const server = http.createServer((req, res) => {
  const options = {
    hostname: TARGET_HOST,
    port: TARGET_PORT,
    path: req.url,
    method: req.method,
    headers: req.headers
  };

  const proxyReq = http.request(options, (proxyRes) => {
    res.writeHead(proxyRes.statusCode, proxyRes.headers);
    proxyRes.pipe(res, { end: true });
  });

  proxyReq.on('error', (err) => {
    res.writeHead(502, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'Zyntra RFID Lab local backend offline: ' + err.message }));
  });

  req.pipe(proxyReq, { end: true });
});

server.listen(PROXY_PORT, '0.0.0.0', () => {
  console.log(`Wi-Fi Mobile Gateway active on http://192.168.68.112:${PROXY_PORT}`);
});
