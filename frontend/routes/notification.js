const express = require('express');
const router = express.Router();
const httpProxy = require('express-http-proxy');
const ServiceLocation = require('../consul/serviceLocation');

// Proxy requests to notification-service via Consul
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
