package com.nebula.mobile

import android.content.Context
import android.os.Bundle
import android.content.Intent
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import kotlinx.coroutines.launch
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import retrofit2.http.*
import com.google.zxing.integration.android.IntentIntegrator

data class PairRequest(val schema:Int=1,val serverId:String,val pairingKey:String)
data class PairResponse(val accessToken:String?=null)
data class JobRequest(val Url:String,val Quality:String="720",val Format:String="mp4",val AudioQuality:String="128",val FastMode:Boolean=true,val DataSaverMode:Boolean=false,val IsPlaylist:Boolean=false)
interface NebulaApi { @POST("auth/pair") suspend fun pair(@Body body:PairRequest):PairResponse; @GET("health") suspend fun health():Map<String,Any>; @GET("dashboardapp") suspend fun dashboard():Map<String,Any>; @POST("jobs") suspend fun create(@Body body:JobRequest):Map<String,Any>; @GET("jobs/{id}") suspend fun job(@Path("id") id:String):Map<String,Any>; @GET("jobs/history") suspend fun history():List<Map<String,Any>>; @DELETE("jobs/history") suspend fun clearHistory(); @POST("images/analyze") suspend fun images(@Body body:Map<String,String>):Map<String,Any> }
class MainActivity:ComponentActivity(){
    private var scanListener: ((String)->Unit)? = null
    override fun onCreate(b:Bundle?){super.onCreate(b);setContent{NebulaApp(this,{ scanListener = it },{ scanQr() })}}
    fun scanQr(){ IntentIntegrator(this).setDesiredBarcodeFormats(IntentIntegrator.QR_CODE).setPrompt("Scan the NebulaServer pairing QR").setBeepEnabled(true).setOrientationLocked(false).initiateScan() }
    override fun onActivityResult(requestCode:Int,resultCode:Int,data:Intent?){ super.onActivityResult(requestCode,resultCode,data); val result=IntentIntegrator.parseActivityResult(requestCode,resultCode,data); if(result?.contents!=null) scanListener?.invoke(result.contents) }
}
@Composable fun NebulaApp(context:Context,registerScan:((String)->Unit)->Unit,startScan:()->Unit){ var base by remember{mutableStateOf(context.getSharedPreferences("nebula",0).getString("url","") ?: "")}; var token by remember{mutableStateOf(context.getSharedPreferences("nebula",0).getString("token","") ?: "")}; var tab by remember{mutableIntStateOf(0)}; var api by remember(base){ mutableStateOf(if(base.isBlank()) null else client(base,token)) }; MaterialTheme(colorScheme=darkColorScheme(primary=Color(0xff55e6ff),background=Color(0xff070b18),surface=Color(0xff111a31))){ if(base.isBlank()||token.isBlank()) Pairing({u,t->{base=u;token=t;context.getSharedPreferences("nebula",0).edit().putString("url",u).putString("token",t).apply();api=client(u,t)}},registerScan,startScan) else Scaffold(bottomBar={NavigationBar{listOf("Pulse","Download","Images","History").forEachIndexed{i,n->NavigationBarItem(selected=tab==i,onClick={tab=i},icon={},label={Text(n)})}}}){p->Box(Modifier.padding(p)){when(tab){0->Dashboard(api);1->Download(api);2->Images(api);else->History(api)}}}} }
fun client(url:String,token:String)=Retrofit.Builder().baseUrl(url.trimEnd('/')+"/api/").addConverterFactory(GsonConverterFactory.create()).client(okhttp3.OkHttpClient.Builder().addInterceptor{c->c.proceed(c.request().newBuilder().addHeader("Authorization","Bearer $token").build())}.build()).build().create(NebulaApi::class.java)
@Composable fun Pairing(onDone:(String,String)->Unit,registerScan:((String)->Unit)->Unit,startScan:()->Unit){var url by remember{mutableStateOf("http://")};var raw by remember{mutableStateOf("")};var error by remember{mutableStateOf("")};var busy by remember{mutableStateOf(false)};val scope=rememberCoroutineScope();LaunchedEffect(Unit){registerScan{scanned->raw=scanned;runCatching{val j=JsonParser.parseString(scanned).asJsonObject;if(j.has("localIp"))url="http://${j["localIp"].asString}:${j["port"]?.asInt?:5000}"}.onFailure{error="The QR code is not a Nebula pairing code."}}};Column(Modifier.fillMaxSize().padding(24.dp),verticalArrangement=Arrangement.Center){Text("NEBULA LINK",style=MaterialTheme.typography.headlineMedium);Text("Scan the pairing QR shown by NebulaServer.");Spacer(Modifier.height(20.dp));OutlinedTextField(url,{url=it},label={Text("Server URL")},modifier=Modifier.fillMaxWidth());Button(onClick=startScan,modifier=Modifier.fillMaxWidth()){Text("SCAN PAIRING QR")};OutlinedTextField(raw,{raw=it},label={Text("QR payload JSON (fallback)")},modifier=Modifier.fillMaxWidth());Button(onClick={scope.launch{runCatching{busy=true;val j=JsonParser.parseString(raw).asJsonObject;val response=client(url,"").pair(PairRequest(serverId=j["serverId"].asString,pairingKey=j["pairingKey"].asString));onDone(url,response.accessToken.orEmpty())}.onFailure{error="Pairing failed: check URL, QR payload, and LAN firewall."};busy=false}},modifier=Modifier.fillMaxWidth()){Text(if(busy)"CONNECTING…" else "CONNECT")};if(error.isNotBlank())Text(error,color=Color.Red)} }
@Composable fun Dashboard(api:NebulaApi?){var text by remember{mutableStateOf("Loading…")};LaunchedEffect(api){runCatching{ text="Connected • "+api!!.health()["status"]}.onFailure{text="Server unavailable"}};Page("NEBULA / PULSE","Command center"){Card(modifier=Modifier.fillMaxWidth()){Column(Modifier.padding(20.dp)){Text(text);Text("Ready for downloads, reels, TikTok videos, and social images.")}}} }
@Composable fun Download(api:NebulaApi?){var url by remember{mutableStateOf("")};var state by remember{mutableStateOf("")};val scope=rememberCoroutineScope();Column(Modifier.fillMaxSize().padding(20.dp)){Text("DOWNLOAD",style=MaterialTheme.typography.labelLarge);Text("Bring it home",style=MaterialTheme.typography.headlineMedium);Spacer(Modifier.height(16.dp));OutlinedTextField(url,{url=it},label={Text("Video / Reel / TikTok URL")},modifier=Modifier.fillMaxWidth());Button(onClick={scope.launch{runCatching{val r=api!!.create(JobRequest(url));state="Queued: "+r["jobId"]}.onFailure{state="Could not start job"}}},modifier=Modifier.fillMaxWidth()){Text("START DOWNLOAD")};Text(state)} }
@Composable fun Images(api:NebulaApi?){var url by remember{mutableStateOf("")};var result by remember{mutableStateOf("")};val scope=rememberCoroutineScope();Column(Modifier.fillMaxSize().padding(20.dp)){Text("IMAGE ENGINE",style=MaterialTheme.typography.labelLarge);Text("Capture the frame",style=MaterialTheme.typography.headlineMedium);Spacer(Modifier.height(16.dp));OutlinedTextField(url,{url=it},label={Text("Instagram / TikTok image URL")},modifier=Modifier.fillMaxWidth());Button(onClick={scope.launch{runCatching{result="Images: "+(api!!.images(mapOf("Url" to url))["images"] as? List<*>)?.size}.onFailure{result="No public images found"}}},modifier=Modifier.fillMaxWidth()){Text("ANALYZE IMAGES")};Text(result)} }
@Composable fun History(api:NebulaApi?){var rows by remember{mutableStateOf(listOf<Map<String,Any>>())};LaunchedEffect(api){runCatching{rows=api!!.history()}};Page("HISTORY","Recent signals"){LazyColumn{items(rows){Text("${it["url"] ?: "Download"} • ${it["status"] ?: ""}",Modifier.padding(12.dp))}}} }
@Composable fun Page(kicker:String,title:String,content: @Composable () -> Unit) { Column(Modifier.fillMaxSize().padding(20.dp)){Text(kicker,color=Color(0xff55e6ff));Text(title,style=MaterialTheme.typography.headlineMedium);Spacer(Modifier.height(20.dp));content()} }
