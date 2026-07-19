import http from 'node:http';

const targetRps = Number(process.env.SOAK_RPS);
const durationMinutes = Number(process.env.SOAK_MINUTES);
const durationMs = durationMinutes * 60_000;
if (!Number.isFinite(durationMs) || durationMs <= 0 || !Number.isFinite(targetRps) || targetRps <= 0) {
  throw new Error('SOAK_MINUTES and SOAK_RPS must be positive numbers');
}

const concurrency = Math.max(1, Math.min(4, targetRps));
const intervalMs = concurrency * 1000 / targetRps;
const startedAt = Date.now();
const deadline = startedAt + durationMs;
const agent = new http.Agent({ keepAlive: true, maxSockets: concurrency });
let requests = 0;
let failures = 0;
let latencyTotalMs = 0;
let latencyMaxMs = 0;
const latenciesMs = [];
const statusCounts = new Map();

function requestOnce() {
  return new Promise((resolve) => {
    const requestStarted = process.hrtime.bigint();
    function recordLatency() {
      const latencyMs = Number(process.hrtime.bigint() - requestStarted) / 1_000_000;
      latencyTotalMs += latencyMs;
      latencyMaxMs = Math.max(latencyMaxMs, latencyMs);
      latenciesMs.push(latencyMs);
    }
    const request = http.get({
      agent,
      hostname: process.env.SOAK_HOST ?? 'localhost',
      port: Number(process.env.SOAK_PORT ?? 8080),
      path: process.env.SOAK_PATH ?? '/health/live',
      headers: { Host: process.env.SOAK_HOST_HEADER ?? 'localhost' },
      timeout: 5_000
    }, (response) => {
      response.resume();
      response.on('end', () => {
        recordLatency();
        requests += 1;
        statusCounts.set(response.statusCode, (statusCounts.get(response.statusCode) ?? 0) + 1);
        if (response.statusCode !== 200) failures += 1;
        resolve();
      });
    });
    request.on('timeout', () => request.destroy(new Error('request timeout')));
    request.on('error', () => {
      recordLatency();
      requests += 1;
      failures += 1;
      resolve();
    });
  });
}

async function worker() {
  while (Date.now() < deadline) {
    const cycleStarted = Date.now();
    await requestOnce();
    const delayMs = Math.max(0, intervalMs - (Date.now() - cycleStarted));
    if (delayMs > 0) await new Promise(resolve => setTimeout(resolve, delayMs));
  }
}

const heartbeat = setInterval(() => {
  console.log(JSON.stringify({
    type: 'heartbeat',
    elapsedMinutes: Number(((Date.now() - startedAt) / 60_000).toFixed(2)),
    requests,
    failures,
    actualRps: Number((requests / Math.max(1, (Date.now() - startedAt) / 1000)).toFixed(2))
  }));
}, 60_000);

await Promise.all(Array.from({ length: concurrency }, worker));
clearInterval(heartbeat);
agent.destroy();
latenciesMs.sort((left, right) => left - right);
const percentile = value => latenciesMs.length === 0
  ? 0
  : latenciesMs[Math.min(latenciesMs.length - 1, Math.ceil(value * latenciesMs.length) - 1)];
console.log(JSON.stringify({
  durationMinutes,
  targetRps,
  concurrency,
  requests,
  failures,
  actualRps: Number((requests / Math.max(1, durationMs / 1000)).toFixed(2)),
  latencyMs: {
    average: Number((latencyTotalMs / Math.max(1, requests)).toFixed(2)),
    p50: Number(percentile(0.50).toFixed(2)),
    p95: Number(percentile(0.95).toFixed(2)),
    p99: Number(percentile(0.99).toFixed(2)),
    max: Number(latencyMaxMs.toFixed(2))
  },
  statusCounts: Object.fromEntries(statusCounts)
}, null, 2));

if (requests === 0 || failures !== 0) process.exitCode = 1;
