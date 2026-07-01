package com.abysalto.middleware.model;

/**
 * Application roles. Persisted by name ({@code @Enumerated(STRING)}) and exposed to Spring Security
 * as {@code ROLE_<name>} authorities. Only a single role exists today; the enum keeps role handling
 * type-safe and gives an obvious place to grow.
 */
public enum Role {
    USER;

    /** Spring Security authority form of this role (e.g. {@code ROLE_USER}). */
    public String authority() {
        return "ROLE_" + name();
    }
}
