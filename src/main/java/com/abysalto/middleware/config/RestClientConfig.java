package com.abysalto.middleware.config;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.http.client.SimpleClientHttpRequestFactory;
import org.springframework.web.client.RestClient;

import java.time.Duration;

/**
 * Builds the {@link RestClient} used by the DummyJSON source, with connect/read timeouts so a slow or
 * unreachable upstream fails fast (surfacing as a 502) rather than hanging the request.
 *
 * <p>{@code RestClient} is the synchronous, servlet-stack-native HTTP client (from {@code spring-web}),
 * so the middleware carries no reactive runtime just to block on it.
 */
@Configuration
public class RestClientConfig {

    @Bean
    public RestClient dummyJsonRestClient(UpstreamProperties properties) {
        SimpleClientHttpRequestFactory factory = new SimpleClientHttpRequestFactory();
        factory.setConnectTimeout(Duration.ofMillis(properties.connectTimeoutMs()));
        factory.setReadTimeout(Duration.ofMillis(properties.responseTimeoutMs()));

        return RestClient.builder()
                .baseUrl(properties.baseUrl())
                .requestFactory(factory)
                .build();
    }
}
