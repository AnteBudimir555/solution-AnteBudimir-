package com.abysalto.middleware.config;

import io.netty.channel.ChannelOption;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.http.client.reactive.ReactorClientHttpConnector;
import org.springframework.web.reactive.function.client.WebClient;
import reactor.netty.http.client.HttpClient;

import java.time.Duration;

/**
 * Builds the {@link WebClient} used by the DummyJSON source, with connect/response timeouts so a
 * slow or unreachable upstream fails fast and surfaces as a 502 rather than hanging the request.
 */
@Configuration
public class WebClientConfig {

    @Bean
    public WebClient dummyJsonWebClient(WebClient.Builder builder, UpstreamProperties properties) {
        HttpClient httpClient = HttpClient.create()
                .option(ChannelOption.CONNECT_TIMEOUT_MILLIS, properties.connectTimeoutMs())
                .responseTimeout(Duration.ofMillis(properties.responseTimeoutMs()));

        return builder
                .baseUrl(properties.baseUrl())
                .clientConnector(new ReactorClientHttpConnector(httpClient))
                .build();
    }
}
