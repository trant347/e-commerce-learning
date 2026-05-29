package com.bookstore.productsevice;

import com.bookstore.productsevice.model.TaskMaster;
import com.jayway.jsonpath.JsonPath;
import org.junit.Assert;
import org.junit.Before;
import org.junit.ClassRule;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.mockito.Mock;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.annotation.ComponentScan;
import org.springframework.http.HttpHeaders;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.TestPropertySource;
import org.springframework.test.context.junit4.SpringRunner;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import org.testcontainers.containers.DockerComposeContainer;
import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;

import java.util.List;
import java.util.Locale;

import static org.junit.Assert.assertEquals;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;
@ActiveProfiles(value = "test")
@RunWith(SpringRunner.class)
@SpringBootTest(
        properties={"spring.cloud.consul.enabled=false", "spring.cloud.consul.config.enabled=false","spring.cloud.consul.binder.enabled=false"
                        })
@AutoConfigureMockMvc
public class BookServiceApplicationTests {


    @ClassRule
    public static DockerComposeContainer compose = StartStopContainers.startExternalServices();

    @Autowired
    private MockMvc mockMvc;
    public ObjectMapper objectMapper = new ObjectMapper();



    @Test
    public void shouldReturnListOfTaskMasters() throws Exception {
        MvcResult mockMvcResult = this.mockMvc.perform(get("/products")).andExpect(status().isOk())
                                    .andReturn();
        List<TaskMaster> list = objectMapper.readValue(mockMvcResult.getResponse().getContentAsString(), new TypeReference<List<TaskMaster>>(){});
        Assert.assertTrue(list.stream().anyMatch(tm -> tm.getName().length() > 0));
    }


    @Test
    public void shouldReturnCorrectTaskMaster() throws Exception {
        MvcResult mockMvcResult = this.mockMvc.perform(get("/products")).andExpect(status().isOk())
                .andReturn();
        List<TaskMaster> list = objectMapper.readValue(mockMvcResult.getResponse().getContentAsString(), new TypeReference<List<TaskMaster>>(){});
        String id = list.get(0).getId();

        mockMvcResult = this.mockMvc.perform(get("/products/"+id)).andExpect(status().isOk()).andReturn();
        TaskMaster taskMaster = objectMapper.readValue(mockMvcResult.getResponse().getContentAsString(), new TypeReference<TaskMaster>(){});

        Assert.assertTrue(taskMaster.getName().equals(list.get(0).getName()));
    }

    @Test
    public void shouldReturnCorrectErrorMessage() throws Exception {
        MvcResult mockMvcResult = this.mockMvc.perform(get("/products?name=noname").header(HttpHeaders.ACCEPT_LANGUAGE, new Locale("zz"))).andExpect(status().is(400))
                .andReturn();
        String localizedErrorSummary = JsonPath.read(mockMvcResult.getResponse().getContentAsString(), "$.localizedErrorSummary.value");
        assertEquals(localizedErrorSummary, "zz:Validation failed");
    }
}

