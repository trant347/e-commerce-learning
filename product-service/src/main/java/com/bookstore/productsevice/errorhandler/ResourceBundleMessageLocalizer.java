package com.bookstore.productsevice.errorhandler;

import org.springframework.stereotype.Component;

import java.text.MessageFormat;
import java.util.Locale;
import java.util.ResourceBundle;


@Component
public class ResourceBundleMessageLocalizer implements MessageLocalizer {
    @Override
    public LocalizedMessage getLocalized(Locale locale, String resourceBundle, String key, String defaultString, Object... params) {
        ResourceBundle bundle = ResourceBundle.getBundle(resourceBundle,locale);

        Locale bundleLocale = bundle.getLocale();

        if(bundleLocale.getLanguage().isEmpty()) {
            bundleLocale = Locale.getDefault();
        }

        String message = bundle.getString(key);

        if(message != null) {
            return new LocalizedMessage(bundleLocale, formatMessage(message, params));
        }

        return new LocalizedMessage(bundleLocale, defaultString);

    }


    private String formatMessage(String text, Object[] args) {
        return MessageFormat.format(text,args);
    }

}
