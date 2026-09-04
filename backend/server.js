const http = require('node:http');

const PORT = process.env.PORT || 3001;

const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ status: 'ok', message: 'astvard backend is alive' }));
});

server.listen(PORT, () => {
  console.log(`astvard backend listening on port ${PORT}`);
});
