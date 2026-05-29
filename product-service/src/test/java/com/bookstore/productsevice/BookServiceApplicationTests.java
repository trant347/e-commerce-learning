package com.bookstore.productsevice;

import com.bookstore.productsevice.model.TaskMaster;
import org.junit.Assert;
import org.junit.ClassRule;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.junit4.SpringRunner;
import org.springframework.cloud.stream.binder.test.TestChannelBinderConfiguration;
import org.springframework.context.annotation.Import;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import org.testcontainers.containers.DockerComposeContainer;
import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;

import java.util.List;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;
@ActiveProfiles(value = "test")
@RunWith(SpringRunner.class)
@SpringBootTest(
        properties={"spring.cloud.consul.enabled=false", "spring.cloud.consul.config.enabled=false","spring.cloud.consul.binder.enabled=false",
                "spring.kafka.listener.auto-startup=false", "spring.kafka.bootstrap-servers=localhost:9092",
                "spring.data.mongodb.host=localhost"
                        })
@AutoConfigureMockMvc
@Import(TestChannelBinderConfiguration.class)
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
}

