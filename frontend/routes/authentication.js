var express = require('express');
var router = express.Router();
var proxy = require('express-http-proxy');

var endpoints = require('../consul/serviceLocation');

function getHost() {
    return `${endpoints.getServiceLocationPath("authentication-service")}`;
}

module.exports = proxy(getHost,{
    memoizeHost: false,
    proxyReqPathResolver: function (req) {        
       console.log(`${endpoints.getServiceLocationPath("authentication-service")}`)
       let updatedPath = req.originalUrl.replace('/user/','/');
       console.log(updatedPath);
       return updatedPath;
    }
});
