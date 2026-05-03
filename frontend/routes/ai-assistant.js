const express = require('express');
const router = express.Router();
const httpProxy = require('express-http-proxy');
const ServiceLocation = require('../consul/serviceLocation');

router.use('/', httpProxy(() => ServiceLocation.getServiceLocationPath('ai-assistant-service'), {
    proxyReqPathResolver: (req) => {
        return `/api/ai-assistant${req.url}`;
    }
}));

module.exports = router;
