package com.abysalto.middleware.config;

import com.abysalto.middleware.model.Role;
import com.abysalto.middleware.model.UserAccount;
import com.abysalto.middleware.repository.UserRepository;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.ApplicationArguments;
import org.springframework.boot.ApplicationRunner;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Component;

/**
 * Creates the configured seed user on startup (when enabled and not already present) so the API
 * can be exercised without a manual registration step. The password is stored only as a BCrypt hash.
 */
@Component
public class UserSeeder implements ApplicationRunner {

    private static final Logger log = LoggerFactory.getLogger(UserSeeder.class);
    private static final Role DEFAULT_ROLE = Role.USER;

    private final SeedUserProperties props;
    private final UserRepository users;
    private final PasswordEncoder passwordEncoder;

    public UserSeeder(SeedUserProperties props, UserRepository users, PasswordEncoder passwordEncoder) {
        this.props = props;
        this.users = users;
        this.passwordEncoder = passwordEncoder;
    }

    @Override
    public void run(ApplicationArguments args) {
        if (!props.enabled()) {
            return;
        }
        if (users.existsByUsername(props.username())) {
            log.info("Seed user '{}' already exists; skipping.", props.username());
            return;
        }
        UserAccount user = new UserAccount(
                props.username(),
                passwordEncoder.encode(props.password()),
                DEFAULT_ROLE);
        users.save(user);
        log.info("Seeded user '{}' with role {}.", props.username(), DEFAULT_ROLE);
    }
}
