package com.abysalto.middleware;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.ConfigurationPropertiesScan;

@SpringBootApplication
@ConfigurationPropertiesScan
public class MiddlewareApplication {

	public static void main(String[] args) {
		SpringApplication.run(MiddlewareApplication.class, args);
	}

}
