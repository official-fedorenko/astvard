const dgram = require('node:dgram');

const A2S_QUERY_HEADER = Buffer.from([0xff, 0xff, 0xff, 0xff, 0x54]);
const A2S_QUERY_TAIL = Buffer.from('Source Engine Query\0', 'ascii');

function readCString(buf, offset) {
  const end = buf.indexOf(0, offset);
  return { value: buf.toString('utf8', offset, end), next: end + 1 };
}

function parseInfoResponse(buf) {
  // header(4) + type byte(0x49) already stripped by caller
  let offset = 1; // protocol version byte
  const name = readCString(buf, offset); offset = name.next;
  const map = readCString(buf, offset); offset = map.next;
  const folder = readCString(buf, offset); offset = folder.next;
  const game = readCString(buf, offset); offset = game.next;
  offset += 2; // appid (int16)
  const players = buf.readUInt8(offset); offset += 1;
  const maxPlayers = buf.readUInt8(offset);
  return { name: name.value, map: map.value, game: game.value, players, maxPlayers };
}

// Queries the Source-engine-style A2S_INFO protocol, which Valheim (and most
// Steam dedicated servers) answer on their game UDP port for server browsers.
function queryA2SInfo(host, port, timeoutMs = 2000) {
  return new Promise((resolve) => {
    const socket = dgram.createSocket('udp4');
    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      socket.close();
      resolve(result);
    };

    const timer = setTimeout(() => finish({ online: false }), timeoutMs);

    function send(extra) {
      const packet = extra
        ? Buffer.concat([A2S_QUERY_HEADER, A2S_QUERY_TAIL, extra])
        : Buffer.concat([A2S_QUERY_HEADER, A2S_QUERY_TAIL]);
      socket.send(packet, port, host);
    }

    socket.on('error', () => { clearTimeout(timer); finish({ online: false }); });

    socket.on('message', (msg) => {
      // A throw in here is uncaught: the promise executor has already returned, so
      // a five-byte read on a shorter reply would kill the process, not the query.
      if (msg.length < 5) return;

      const type = msg.readUInt8(4);
      if (type === 0x41) {
        // Challenge response: resend query with the challenge bytes appended.
        send(msg.subarray(5, 9));
        return;
      }
      if (type === 0x49) {
        clearTimeout(timer);
        try {
          finish({ online: true, ...parseInfoResponse(msg.subarray(5)) });
        } catch {
          finish({ online: true });
        }
      }
    });

    send();
  });
}

module.exports = { queryA2SInfo };
