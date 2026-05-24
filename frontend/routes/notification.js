const express = require('express');
const http = require('http');
const url = require('url');
const router = express.Router();
const httpProxy = require('express-http-proxy');
const ServiceLocation = require('../consul/serviceLocation');

// SSE stream endpoint — must bypass express-http-proxy because its
// userResDecorator buffers the entire response, breaking streaming.
router.get('/:userId/stream', (req, res) => {
    const upstreamBase = ServiceLocation.getServiceLocationPath('notification-service');
    const upstreamUrl = `${upstreamBase}/api/notification/${req.params.userId}/stream`;
    console.log(`[BFF][notification] → SSE ${upstreamUrl}`);

    const parsed = url.parse(upstreamUrl);
    const upstreamReq = http.request({
        hostname: parsed.hostname,
        port: parsed.port,
        path: parsed.path,
        method: 'GET',
        headers: { Accept: 'text/event-stream' }
    }, (upstreamRes) => {
        res.writeHead(upstreamRes.statusCode, {
            'Content-Type': 'text/event-stream',
            'Cache-Control': 'no-cache',
            'Connection': 'keep-alive',
            'X-Accel-Buffering': 'no'
        });
        upstreamRes.pipe(res);
    });

    upstreamReq.on('error', (err) => {
        console.error(`[BFF][notification] ✗ SSE upstream error | ${err.message}`);
        if (!res.headersSent) res.status(502).end();
    });

    req.on('close', () => upstreamReq.destroy());
    upstreamReq.end();
});

// All other notification endpoints go through the buffering proxy.
router.use('/', httpProxy((req) => ServiceLocation.getServiceLocationPath('notification-service'), {
    proxyReqPathResolver: (req) => {
        const upstream = `${ServiceLocation.getServiceLocationPath('notification-service')}/api/notification${req.url}`;
        console.log(`[BFF][notification] → ${req.method} ${upstream}`);
        return `/api/notification${req.url}`;
    },
    userResDecorator: (proxyRes, proxyResData, userReq) => {
        console.log(`[BFF][notification] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.url}`);
        return proxyResData;
    },
    proxyErrorHandler: (err, res, next) => {
        console.error(`[BFF][notification] ✗ proxy error | upstream: ${ServiceLocation.getServiceLocationPath('notification-service')} | ${err.message}`);
        next(err);
    }
}));

module.exports = router;
