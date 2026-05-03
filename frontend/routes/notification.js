const express = require('express');
const router = express.Router();
const httpProxy = require('express-http-proxy');
const ServiceLocation = require('../consul/serviceLocation');

// Proxy requests to notification-service via Consul
router.use('/', httpProxy((req) => ServiceLocation.getServiceLocationPath('notification-service'), {
    proxyReqPathResolver: (req) => {
        return `/api/notification${req.url}`;
    }
}));

module.exports = router;
