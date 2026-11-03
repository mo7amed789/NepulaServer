plugins { alias(libs.plugins.android.application); alias(libs.plugins.kotlin.android); alias(libs.plugins.kotlin.compose) }
android { namespace = "com.nebula.mobile"; compileSdk = 36
    defaultConfig { applicationId = "com.nebula.mobile"; minSdk = 24; targetSdk = 36; versionCode = 1; versionName = "1.0" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_11; targetCompatibility = JavaVersion.VERSION_11 }
    kotlinOptions { jvmTarget = "11" }
    buildFeatures { compose = true; buildConfig = true }
}
dependencies { implementation(libs.androidx.core.ktx); implementation(libs.androidx.activity.compose); implementation(platform(libs.androidx.compose.bom)); implementation(libs.androidx.compose.ui); implementation(libs.androidx.compose.ui.graphics); implementation(libs.androidx.compose.ui.tooling.preview); implementation(libs.androidx.compose.material3); implementation(libs.retrofit); implementation(libs.converter.gson); implementation("com.journeyapps:zxing-android-embedded:4.3.0"); debugImplementation(libs.androidx.compose.ui.tooling) }
