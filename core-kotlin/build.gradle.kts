plugins {
    kotlin("jvm") version "2.4.21"
    kotlin("plugin.serialization") version "2.4.21"
}

allprojects {
    group = "com.n3k0chan.klip"
    version = "1.0.0"
}

kotlin {
    // Java 17 bytecode so the Android app (minSdk 29) can consume this module.
    jvmToolchain(17)
}

dependencies {
    api("org.jetbrains.kotlinx:kotlinx-serialization-json:1.13.0")

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
