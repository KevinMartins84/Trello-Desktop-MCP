import crypto from 'node:crypto';
import http from 'node:http';

const durationLabels = {
  '1d': '1 day',
  '1w': '1 week',
  '1m': '1 month',
  '6m': '6 months',
  '1y': '1 year',
  always: 'always'
};

const durationSeconds = {
  '1d': 24 * 60 * 60,
  '1w': 7 * 24 * 60 * 60,
  '1m': 30 * 24 * 60 * 60,
  '6m': 180 * 24 * 60 * 60,
  '1y': 365 * 24 * 60 * 60
};

const sessions = new Map();
const pendingStates = new Map();

const port = Number(process.env.PORT ?? 8080);
const defaultApiKey = process.env.TRELLO_API_KEY ?? '';
const appBaseUrl = process.env.APP_BASE_URL ?? `http://localhost:${port}`;
const callbackPath = '/auth/callback';
const callbackUrl = `${appBaseUrl}${callbackPath}`;

function htmlTemplate(content) {
  return `<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Trello OAuth Login</title>
<style>
body{font-family:Arial,sans-serif;background:#f3f4f6;margin:0;padding:30px}
.card{max-width:560px;margin:0 auto;background:#fff;border-radius:12px;padding:24px;box-shadow:0 4px 20px rgba(0,0,0,.08)}
h1{margin-top:0}label{display:block;margin:12px 0 6px;font-weight:700}
input,select,button{width:100%;font-size:14px;padding:10px;border-radius:8px;border:1px solid #d1d5db;box-sizing:border-box}
button{margin-top:16px;cursor:pointer;border:0;color:#fff;background:#2563eb;font-weight:700}
button:hover{background:#1d4ed8}.muted{color:#6b7280;font-size:13px;margin-top:10px}
.ok{color:#065f46;background:#d1fae5;border-radius:8px;padding:10px}
.error{color:#991b1b;background:#fee2e2;border-radius:8px;padding:10px}
code{font-family:Consolas,monospace}
</style></head><body><main class="card">${content}</main></body></html>`;
}

function parseCookies(cookieHeader = '') {
  return cookieHeader.split(';').reduce((acc, part) => {
    const [rawName, ...rawValue] = part.trim().split('=');
    if (!rawName) return acc;
    acc[rawName] = decodeURIComponent(rawValue.join('='));
    return acc;
  }, {});
}

function parseFormBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk.toString();
      if (body.length > 1024 * 1024) {
        reject(new Error('Payload too large'));
      }
    });
    req.on('end', () => resolve(new URLSearchParams(body)));
    req.on('error', reject);
  });
}

function getDurationOptions(selected = '1w') {
  return Object.keys(durationLabels)
    .map((value) => `<option value="${value}" ${selected === value ? 'selected' : ''}>${durationLabels[value]}</option>`)
    .join('');
}

function computeExpiry(duration) {
  if (duration === 'always') return null;
  return Date.now() + durationSeconds[duration] * 1000;
}

function setCookie(res, name, value, options = {}) {
  const parts = [`${name}=${encodeURIComponent(value)}`];
  if (options.httpOnly) parts.push('HttpOnly');
  if (options.sameSite) parts.push(`SameSite=${options.sameSite}`);
  if (options.secure) parts.push('Secure');
  parts.push('Path=/');
  if (options.maxAge) parts.push(`Max-Age=${Math.floor(options.maxAge / 1000)}`);
  res.setHeader('Set-Cookie', parts.join('; '));
}

function clearCookie(res, name) {
  res.setHeader('Set-Cookie', `${name}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax`);
}

function redirect(res, location) {
  res.statusCode = 302;
  res.setHeader('Location', location);
  res.end();
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url || '/', `http://${req.headers.host}`);
    const path = url.pathname;

    if (req.method === 'GET' && path === '/') {
      res.setHeader('Content-Type', 'text/html; charset=utf-8');
      res.end(
        htmlTemplate(`
          <h1>Trello OAuth login</h1>
          <p>Sign in with Trello and choose how long the authentication should stay valid.</p>
          <form method="post" action="/auth/start">
            <label for="apiKey">Trello API key</label>
            <input id="apiKey" name="apiKey" required value="${defaultApiKey}" />
            <label for="duration">Authentication validity</label>
            <select id="duration" name="duration">${getDurationOptions()}</select>
            <button type="submit">Authenticate with Trello</button>
          </form>
          <p class="muted">Callback URL configured as <code>${callbackUrl}</code>.</p>
        `)
      );
      return;
    }

    if (req.method === 'POST' && path === '/auth/start') {
      const body = await parseFormBody(req);
      const apiKey = String(body.get('apiKey') || '').trim();
      const duration = String(body.get('duration') || '1w');

      if (!apiKey) {
        res.statusCode = 400;
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        res.end(htmlTemplate('<p class="error">Trello API key is required.</p>'));
        return;
      }

      if (!(duration in durationLabels)) {
        res.statusCode = 400;
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        res.end(htmlTemplate('<p class="error">Unsupported duration option.</p>'));
        return;
      }

      const state = crypto.randomUUID();
      pendingStates.set(state, { apiKey, duration });
      const expires = duration === 'always' ? 'never' : String(durationSeconds[duration]);

      const authorizeUrl = new URL('https://trello.com/1/authorize');
      authorizeUrl.searchParams.set('key', apiKey);
      authorizeUrl.searchParams.set('name', 'Trello Desktop MCP OAuth');
      authorizeUrl.searchParams.set('scope', 'read,write');
      authorizeUrl.searchParams.set('expiration', expires);
      authorizeUrl.searchParams.set('response_type', 'token');
      authorizeUrl.searchParams.set('callback_method', 'fragment');
      authorizeUrl.searchParams.set('return_url', `${callbackUrl}?state=${encodeURIComponent(state)}`);

      redirect(res, authorizeUrl.toString());
      return;
    }

    if (req.method === 'GET' && path === callbackPath) {
      const state = String(url.searchParams.get('state') || '');
      const token = String(url.searchParams.get('token') || '');

      if (!token) {
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        res.end(
          htmlTemplate(`
            <h1>Finalizing Trello authentication...</h1>
            <p class="muted">Please wait, we are capturing your token.</p>
            <script>
              const hash = new URLSearchParams(window.location.hash.slice(1));
              const token = hash.get('token');
              if (!token) {
                document.querySelector('main').innerHTML = '<p class="error">Token not found in callback URL.</p>';
              } else {
                const u = new URL(window.location.origin + '${callbackPath}');
                u.searchParams.set('state', '${encodeURIComponent(state)}');
                u.searchParams.set('token', token);
                window.location.replace(u.toString());
              }
            </script>
          `)
        );
        return;
      }

      const pending = pendingStates.get(state);
      if (!pending) {
        res.statusCode = 400;
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        res.end(htmlTemplate('<p class="error">State is invalid or expired. Start auth again.</p>'));
        return;
      }

      pendingStates.delete(state);
      const sessionId = crypto.randomUUID();
      const expiresAt = computeExpiry(pending.duration);
      sessions.set(sessionId, {
        id: sessionId,
        apiKey: pending.apiKey,
        token,
        createdAt: Date.now(),
        expiresAt
      });

      setCookie(res, 'trello_session', sessionId, {
        httpOnly: true,
        sameSite: 'Lax',
        secure: appBaseUrl.startsWith('https://'),
        maxAge: pending.duration === 'always' ? undefined : durationSeconds[pending.duration] * 1000
      });

      const expiryLabel = expiresAt ? new Date(expiresAt).toISOString() : 'never (always)';
      res.setHeader('Content-Type', 'text/html; charset=utf-8');
      res.end(
        htmlTemplate(`
          <h1>Trello authenticated</h1>
          <p class="ok">Authentication successful and session is active.</p>
          <p><strong>Valid until:</strong> ${expiryLabel}</p>
          <p class="muted">Check <code>/session</code> for your active Trello API key + token pair.</p>
        `)
      );
      return;
    }

    if (req.method === 'GET' && path === '/session') {
      const cookies = parseCookies(req.headers.cookie);
      const sessionId = cookies.trello_session;
      if (!sessionId || !sessions.has(sessionId)) {
        res.statusCode = 401;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ authenticated: false, message: 'No active Trello session' }));
        return;
      }

      const session = sessions.get(sessionId);
      if (session.expiresAt && Date.now() > session.expiresAt) {
        sessions.delete(sessionId);
        clearCookie(res, 'trello_session');
        res.statusCode = 401;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ authenticated: false, message: 'Session expired' }));
        return;
      }

      res.setHeader('Content-Type', 'application/json');
      res.end(
        JSON.stringify({
          authenticated: true,
          apiKey: session.apiKey,
          token: session.token,
          expiresAt: session.expiresAt
        })
      );
      return;
    }

    if (req.method === 'POST' && path === '/logout') {
      const cookies = parseCookies(req.headers.cookie);
      if (cookies.trello_session) {
        sessions.delete(cookies.trello_session);
      }
      clearCookie(res, 'trello_session');
      redirect(res, '/');
      return;
    }

    res.statusCode = 404;
    res.setHeader('Content-Type', 'text/plain; charset=utf-8');
    res.end('Not found');
  } catch (error) {
    res.statusCode = 500;
    res.setHeader('Content-Type', 'text/plain; charset=utf-8');
    res.end(`Internal server error: ${error instanceof Error ? error.message : String(error)}`);
  }
});

server.listen(port, () => {
  console.log(`OAuth server listening on ${appBaseUrl}`);
});
