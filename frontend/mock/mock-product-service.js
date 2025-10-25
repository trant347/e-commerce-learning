var express = require('express');
var router = express.Router();


const products = [
    {
        id: 1,
        name: "God Father",
        priceUsd: 100,
        description: "The story of an underworld crime boss",
        authors: ["Mario Puzzo"]
    },
    {
        id: 2,
        name: "The Romance of Three Kingdom",
        priceUsd: 20,
        description: "The wars between three ancient kingdoms",
        authors: ["La Quan Trung"]
    }
];




router.get('/', async function(req, res, next){
    try {
        let listOfProducts = products;
        res.send(listOfProducts);
    } catch (e) {
        next(e);
    }
});


router.get('/:id', async (req, res, next) => {

    try {
        let response = products.find(product => product.id == req.params.id);
        res.send(response);
    } catch (e) {
        next(e);
    }

});

module.exports = router;