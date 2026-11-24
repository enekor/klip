plugins {
    kotlin("jvm") version "2.2.20"
    kotlin("plugin.serialization") version "2.2.20"
}

group = "com.n3k0chan.klip"
version = "0.1.0"

kotlin {
    // Java 17 bytecode so the Android app (minSdk 29, desugared) can consume this module.
    jvmToolchain(17)
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")

    testImplementation(kotlin("test"))
}

tasks.test {
    useJUnitPlatform()
    // Shared vectors live at the repo root.
    systemProperty("klip.vectors", rootDir.resolve("../protocol/test-vectors.json").absolutePath)
    testLogging {
        events("passed", "failed", "skipped")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
