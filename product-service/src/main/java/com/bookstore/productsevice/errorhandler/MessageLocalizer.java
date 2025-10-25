package com.bookstore.productsevice.errorhandler;

import java.util.Locale;

public interface MessageLocalizer {

    LocalizedMessage getLocalized(Locale locale, String resourceBundle, String key, String defaultString, Object... params);
}

