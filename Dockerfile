# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage: compile and package the executable jar.
# JDK 25 matches the project's toolchain (Java 21 release level on JDK 25).
# Tests are skipped here — they run in CI via `./mvnw verify`; the image build
# just produces the artifact.
# ---------------------------------------------------------------------------
FROM eclipse-temurin:25-jdk AS build
WORKDIR /workspace

# Copy the Maven wrapper and POM first so dependency resolution caches in its own
# layer and is only redone when the build config changes (not on every source edit).
# The BuildKit cache mount persists ~/.m2 across builds independently of layer
# invalidation, so a pom change re-fetches only what actually changed instead of
# re-downloading every dependency from scratch.
COPY .mvn/ .mvn/
COPY mvnw pom.xml ./
RUN --mount=type=cache,target=/root/.m2 chmod +x mvnw && ./mvnw -B -q dependency:go-offline

# Copy sources and build the repackaged (fat) jar. Same cache mount so the package
# step reuses the resolved dependencies rather than re-downloading them.
COPY src/ src/
RUN --mount=type=cache,target=/root/.m2 ./mvnw -B -q -DskipTests package

# ---------------------------------------------------------------------------
# Runtime stage: minimal JRE with only the packaged jar. The bytecode is at
# release level 21, so a JRE 21 runtime is sufficient.
# ---------------------------------------------------------------------------
FROM eclipse-temurin:21-jre AS runtime
WORKDIR /app

# Run as an unprivileged user rather than root.
RUN groupadd --system app && useradd --system --gid app --home /app app

COPY --from=build /workspace/target/middleware-*.jar app.jar
USER app

EXPOSE 8080
ENTRYPOINT ["java", "-jar", "/app/app.jar"]
