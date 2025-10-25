package com.bookstore.productsevice.storage;

import org.springframework.core.io.Resource;
import org.springframework.web.multipart.MultipartFile;


import java.nio.file.Path;
import java.util.stream.Stream;

public interface StorageService {

    void init();

    String store(MultipartFile file);

    String store(MultipartFile file, String filePath);

    Stream<Path> loadAll();

    Path load(String filename);

    Resource loadAsResource(String filename) throws Exception;

    void delete(String filename);

    void deleteAll();
}
