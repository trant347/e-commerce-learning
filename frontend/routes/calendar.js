var express = require('express');
var router = express.Router();
var proxy = require('express-http-proxy');

var endpoints = require('../consul/serviceLocation');

function getHost() {
    return `${endpoints.getServiceLocationPath("calendar-service")}`;
}

module.exports = proxy(getHost,{
    memoizeHost: false,
    proxyReqPathResolver: function (req) {        
       console.log(`${endpoints.getServiceLocationPath("calendar-service")}`);
       let updatedPath = req.originalUrl.replace('/calendar-service/','/');
       console.log(updatedPath);
       return updatedPath;
    },
    proxyErrorHandler: function (err, res, next) {
        console.log(err);
        next(err);
    }
});
