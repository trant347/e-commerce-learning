const express = require('express');
const router = express.Router();
const httpProxy = require('express-http-proxy');
const ServiceLocation = require('../consul/serviceLocation');

router.use('/', httpProxy(() => ServiceLocation.getServiceLocationPath('ai-assistant-service'), {
    proxyReqPathResolver: (req) => {
        const upstream = `${ServiceLocation.getServiceLocationPath('ai-assistant-service')}/api/ai-assistant${req.url}`;
        console.log(`[BFF][ai-assistant] → ${req.method} ${upstream}`);
        return `/api/ai-assistant${req.url}`;
    },
    userResDecorator: (proxyRes, proxyResData, userReq) => {
        console.log(`[BFF][ai-assistant] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.url}`);
        return proxyResData;
    },
    proxyErrorHandler: (err, res, next) => {
        console.error(`[BFF][ai-assistant] ✗ proxy error | upstream: ${ServiceLocation.getServiceLocationPath('ai-assistant-service')} | ${err.message}`);
        next(err);
    }
}));

module.exports = router;
