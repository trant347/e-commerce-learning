var createError = require('http-errors');
var express = require('express');
var path = require('path');
var cookieParser = require('cookie-parser');
var logger = require('morgan');


var indexRouter = require('./routes/index');
var productRouter = require('./routes/product');
var authenticationRouter = require('./routes/authentication');
const calendarRouter = require('./routes/calendar');
const notificationRouter = require('./routes/notification');
const aiAssistantRouter = require('./routes/ai-assistant');

var app = express();


app.use(logger('dev'));
app.use(express.json());
app.use(express.urlencoded({ extended: false }));
app.use(cookieParser());




app.set('views', path.join(__dirname, './public'));


app.use(express.static(path.join(__dirname, './public')));


app.use('/products', productRouter);

app.use('/user', authenticationRouter);

app.use('/calendar-service', calendarRouter);

app.use('/api/notification', notificationRouter);
app.use('/api/ai-assistant', aiAssistantRouter);

app.use('*',indexRouter);


// catch 404 and forward to error handler
app.use(function(req, res, next) {
  next(createError(404));
});


// error handler
app.use(function(err, req, res, next) {
  const status = err.status || err.response?.status || 500;
  const upstream = err.config?.url || err.address || 'unknown';
  console.error(`[BFF][error] ${req.method} ${req.originalUrl} → ${status} | upstream: ${upstream} | ${err.message}`);
  if (err.stack) console.error(err.stack);

  res.locals.message = err.message;
  res.locals.error = req.app.get('env') === 'development' ? err : {};
  res.status(status);
  res.send(err.message);
});

module.exports = app;
