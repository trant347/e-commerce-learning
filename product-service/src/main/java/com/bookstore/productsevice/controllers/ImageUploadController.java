package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.storage.InvalidFileUploadException;
import com.bookstore.productsevice.storage.StorageService;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.core.io.Resource;
import org.springframework.http.HttpHeaders;
import org.springframework.http.ResponseEntity;
import org.springframework.util.StringUtils;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.multipart.MultipartFile;
import org.springframework.web.servlet.mvc.support.RedirectAttributes;

import java.util.Locale;
import java.util.Set;
import java.util.UUID;

@RestController
public class ImageUploadController {

    private final String IMG_PREFIX = "images/";

    // Keep this in sync with spring.servlet.multipart.max-file-size in application.yml.
    private static final long MAX_FILE_SIZE_BYTES = 2L * 1024 * 1024;

    private static final Set<String> ALLOWED_EXTENSIONS = Set.of("png", "jpg", "jpeg", "pdf");

    @Autowired
    public StorageService storageService;



    @GetMapping("/products/image/{filename:.+}")
    public ResponseEntity<Resource> serveFile(@PathVariable String filename) throws Exception {
        Resource file = storageService.loadAsResource(IMG_PREFIX+ filename);
        return ResponseEntity.ok().header(HttpHeaders.CONTENT_DISPOSITION,"attachment;filename=\""+file.getFilename()+"\"").body(file);
    }

    @PostMapping("/products/image")
    public ResponseEntity<String> handleFileUpdload(@RequestParam("file") MultipartFile file, RedirectAttributes redirectAttributes) {
        String sanitizedFilename = validateAndSanitize(file);
        // Prefix with a random id so two uploads with the same original filename (e.g. two
        // TaskMasters both uploading "photo.jpg") don't silently overwrite each other.
        String uniqueFilename = UUID.randomUUID() + "-" + sanitizedFilename;
        storageService.store(file, IMG_PREFIX + uniqueFilename);
        redirectAttributes.addFlashAttribute("message", String.join("You successfully uploaded "+file.getOriginalFilename()));
        // Return a bare filename (no "images/" prefix): GET /products/image/{filename} re-adds
        // the prefix itself, so including it here would produce a broken "images/images/..." path.
        return ResponseEntity.ok().body(uniqueFilename);
    }

    /**
     * Validates the uploaded file (non-empty, within size limit, accepted extension) and
     * returns a sanitized filename safe to use as a storage path (no directory components).
     */
    private String validateAndSanitize(MultipartFile file) {
        if (file.isEmpty()) {
            throw new InvalidFileUploadException("File is empty");
        }
        if (file.getSize() > MAX_FILE_SIZE_BYTES) {
            throw new InvalidFileUploadException("File exceeds the maximum allowed size of 2MB");
        }

        String cleanedPath = StringUtils.cleanPath(
                file.getOriginalFilename() == null ? "" : file.getOriginalFilename());
        // Strip any directory components the client may have sent; only the base name is kept.
        String baseName = StringUtils.getFilename(cleanedPath);
        if (!StringUtils.hasText(baseName)) {
            throw new InvalidFileUploadException("File name is missing");
        }

        String extension = StringUtils.getFilenameExtension(baseName);
        if (extension == null || !ALLOWED_EXTENSIONS.contains(extension.toLowerCase(Locale.ROOT))) {
            throw new InvalidFileUploadException("Only png, jpg and pdf files are accepted");
        }

        return baseName;
    }
}

