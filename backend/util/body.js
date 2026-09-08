const MAX_BODY_BYTES = 1e6;

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    let raw = '';
    let settled = false;

    // Destroying the stream on its own left the promise pending for ever: 'end'
    // never arrives after a destroy, so the handler awaited a body that could not
    // come. Every path below settles exactly once.
    const finish = (err, value) => {
      if (settled) return;
      settled = true;
      if (err) reject(err);
      else resolve(value);
    };

    req.on('data', (chunk) => {
      raw += chunk;
      if (raw.length > MAX_BODY_BYTES) {
        // Stop accumulating, but leave the stream alone. Destroying it takes the
        // response with it, and pausing it makes the socket error out while the
        // client is still sending — both were tried. The handler answers; Node
        // closes the connection when it does.
        raw = '';
        finish(new Error('Body too large'));
      }
    });
    req.on('end', () => {
      if (!raw) return finish(null, {});
      parse();
    });
    req.on('error', (err) => finish(err));
    req.on('close', () => finish(new Error('Connection closed')));

    function parse() {
      try {
        finish(null, JSON.parse(raw));
      } catch {
        finish(new Error('Invalid JSON body'));
      }
    }
  });
}

module.exports = { readJsonBody };
