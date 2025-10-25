var express = require('express');
var router = express.Router();
var axios = require('axios');


var endpoints = require('../consul/serviceLocation');


router.get('/', async function(req, res, next){
    try {
        let url = `${endpoints.getServiceLocationPath('product-service')}/products`;
        const { page, limit } = req.query;
        if(page && limit) {
            url += `?page=${page}&&limit=${limit}`;
        }
        let listOfProducts = await axios.get(url);
        res.send(listOfProducts.data);
    } catch (e) {
        next(e);
    }
});


router.get('/:id', async (req, res, next) => {

    try {
        let response = await axios.get(
            `${endpoints.getServiceLocationPath('product-service')}/products/${req.params.id}`,
            { headers: {"Authorization" : req.get("Authorization")} }
            );
        res.send(response.data);
    } catch (e) {
        debugger;
        if(e.response && e.response.status == 401) {
            res.sendStatus(401);      
            return;    
        }
        next(e);
    }

});



router.get('/image/:name', async (req, res, next) => {
    try {
        var response = await axios.get(
            `${endpoints.getServiceLocationPath('product-service')}/products/image/${req.params.name}`,
            {
                responseType: "arraybuffer",
                headers: {"Authorization" : req.get("Authorization") || ""}
            })

        var headers = {'Content-Type': 'image/jpeg'};
        res.writeHead(200, headers);
        res.end(response.data, 'binary');


    }catch (e) {
        next(e);
    }
});

module.exports = router;