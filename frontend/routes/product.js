var express = require('express');
var router = express.Router();
var axios = require('axios');


var endpoints = require('../consul/serviceLocation');


router.get('/', async function(req, res, next){
    try {
        let url = `${endpoints.getServiceLocationPath('product-service')}/products`;
        const { page, limit } = req.query;
        if(page && limit) {
            url += `?page=${page}&limit=${limit}`;
        }
        console.log(`[BFF][products] → GET ${url}`);
        let listOfProducts = await axios.get(url);
        console.log(`[BFF][products] ← ${listOfProducts.status} (${listOfProducts.data.length} items)`);
        res.send(listOfProducts.data);
    } catch (e) {
        const upstream = e.config?.url || 'unknown';
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        next(e);
    }
});


router.get('/:id', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/${req.params.id}`;
    try {
        console.log(`[BFF][products] → GET ${upstream}`);
        let response = await axios.get(upstream, { headers: {"Authorization" : req.get("Authorization")} });
        console.log(`[BFF][products] ← ${response.status}`);
        res.send(response.data);
    } catch (e) {
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        if(e.response && e.response.status == 401) {
            res.sendStatus(401);      
            return;    
        }
        next(e);
    }

});



router.get('/image/:name', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/image/${req.params.name}`;
    try {
        console.log(`[BFF][products] → GET ${upstream}`);
        var response = await axios.get(upstream, {
                responseType: "arraybuffer",
                headers: {"Authorization" : req.get("Authorization") || ""}
            });
        console.log(`[BFF][products] ← ${response.status} (image)`);
        var headers = {'Content-Type': 'image/jpeg'};
        res.writeHead(200, headers);
        res.end(response.data, 'binary');


    }catch (e) {
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        next(e);
    }
});

module.exports = router;