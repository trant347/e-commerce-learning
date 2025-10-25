package com.bookstore.productsevice.storage;

import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.autoconfigure.mongo.embedded.EmbeddedMongoProperties;
import org.springframework.core.io.Resource;
import org.springframework.core.io.UrlResource;
import org.springframework.stereotype.Service;
import org.springframework.util.FileSystemUtils;
import org.springframework.util.StringUtils;
import org.springframework.web.multipart.MultipartFile;

import java.io.IOException;
import java.io.InputStream;
import java.net.MalformedURLException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.util.stream.Stream;


@Service
public class FileStorageServiceImpl implements StorageService {


    private final Path rootLocation;

    @Autowired
    public FileStorageServiceImpl(StorageProperties storageProperties) {
        this.rootLocation = Paths.get(storageProperties.getLocation());
        init();
    }

    @Override
    public String store(MultipartFile file) {
        String filename = StringUtils.cleanPath(file.getOriginalFilename());
        return store(file, filename);
    }

    @Override
    public String store(MultipartFile file, String filename) {
        try {
            if(file.isEmpty()) {
                throw new StorageException("Can not store empty file");
            }
            // probably not need
            if(filename.contains("..")) {
                throw new StorageException("Can not store file with relative path outside current directory");
            }
            try(InputStream inputStream = file.getInputStream()) {
                Files.copy(inputStream, this.rootLocation.resolve(filename), StandardCopyOption.REPLACE_EXISTING);
                return filename;
            }
        } catch(IOException e) {
            throw new StorageException(String.join(" " ,"Can not store file ",filename ,e.getMessage()));
        }
    }

    @Override
    public Path load(String filename) {
        return rootLocation.resolve(filename);
    }

    @Override
    public Stream<Path> loadAll() {
        try {
            return Files.walk(this.rootLocation, 1).filter(path -> !path.equals(rootLocation)).map(this.rootLocation::relativize);
        }catch (IOException e) {
            throw new StorageException("Failed to load files ",e);
        }
    }


    @Override
    public Resource loadAsResource(String filename) throws Exception {
        try{
            Path file = load(filename);
            Resource resource = new UrlResource(file.toUri());
            if(resource.exists() || resource.isReadable()) {
                return resource;
            } else {
                throw new StorageException(String.join(" "," Could not read file ", filename ));
            }
        } catch (MalformedURLException e) {
            throw new StorageFileNotFoundException(filename);
        }
    }

    @Override
    public void init() {
        try {
            Files.createDirectories(rootLocation);
        } catch(IOException e) {
            throw new StorageException("Unable to create directory");
        }
    }

    @Override
    public void deleteAll() {
        FileSystemUtils.deleteRecursively(rootLocation.toFile());
    }

    @Override
    public void delete(String filename) {
        FileSystemUtils.deleteRecursively(load(filename).toFile());
    }
}
