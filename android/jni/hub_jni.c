/*
 * Minimal zero-dependency Android ARM64 Bionic JNI bridge for Brazhnichenko Hub.
 * Calls llama_server(argc, argv) inside Android's official classloader-namespace
 * so <uses-native-library> grants access to Qualcomm Hexagon NPU (libcdsprpc.so)
 * and Adreno GPU (libOpenCL.so) on Android 10-17 without root.
 */
#include <stddef.h>
#include <stdarg.h>
#include "jni.h"

/* Standard POSIX / Android Bionic LP64 declarations (no glibc headers needed) */
extern int open(const char* pathname, int flags, unsigned int mode);
extern int dup2(int oldfd, int newfd);
extern int close(int fd);
extern long write(int fd, const void* buf, size_t count);
extern int setenv(const char* name, const char* value, int overwrite);
extern void* dlopen(const char* filename, int flags);
extern void* dlsym(void* handle, const char* symbol);
extern char* dlerror(void);
extern void* calloc(size_t nmemb, size_t size);
extern void free(void* ptr);
extern char* strdup(const char* s);
extern size_t strlen(const char* s);
extern int snprintf(char* str, size_t size, const char* format, ...);

/* Linux / Android ARM64 exact flag values */
#define HUB_O_WRONLY 0x0001
#define HUB_O_CREAT  0x0040
#define HUB_O_APPEND 0x0400
#define HUB_RTLD_NOW 2

typedef int (*llama_server_fn)(int argc, char** argv);

static void log_str(const char* s) {
    if (s) {
        (void)write(2, s, strlen(s));
    }
}

JNIEXPORT jint JNICALL
Java_com_brazhnichenko_hub_LlamaServiceProcess_nativeRunServer(
        JNIEnv* env,
        jclass clazz,
        jobjectArray jArgs,
        jstring jNativeDir,
        jstring jLogPath) {
    (void)clazz;

    const char* nativeDir = jNativeDir ? (*env)->GetStringUTFChars(env, jNativeDir, NULL) : NULL;
    const char* logPath = jLogPath ? (*env)->GetStringUTFChars(env, jLogPath, NULL) : NULL;

    if (logPath != NULL && logPath[0] != '\0') {
        int fd = open(logPath, HUB_O_WRONLY | HUB_O_CREAT | HUB_O_APPEND, 0644);
        if (fd >= 0) {
            dup2(fd, 1);
            dup2(fd, 2);
            close(fd);
        }
    }

    if (nativeDir != NULL) {
        char adsp[1024];
        snprintf(adsp, sizeof(adsp), "%s;/vendor/lib/rfsa/adsp;/system/lib/rfsa/adsp;/dsp", nativeDir);
        setenv("ADSP_LIBRARY_PATH", adsp, 1);
        setenv("DSP_LIBRARY_PATH", adsp, 1);
        setenv("GGML_BACKEND_DIR", nativeDir, 1);
    }

    jsize argc = (*env)->GetArrayLength(env, jArgs);
    char** argv = (char**)calloc((size_t)argc + 1, sizeof(char*));
    for (jsize i = 0; i < argc; i++) {
        jstring js = (jstring)(*env)->GetObjectArrayElement(env, jArgs, i);
        const char* utf = js ? (*env)->GetStringUTFChars(env, js, NULL) : NULL;
        argv[i] = strdup(utf ? utf : "");
        if (utf) (*env)->ReleaseStringUTFChars(env, js, utf);
        if (js) (*env)->DeleteLocalRef(env, js);
    }

    void* handle = dlopen("libllama-server-impl.so", HUB_RTLD_NOW);
    if (!handle) {
        log_str("[HubJNI] dlopen(libllama-server-impl.so) failed: ");
        log_str(dlerror());
        log_str("\n");
        for (jsize i = 0; i < argc; i++) free(argv[i]);
        free(argv);
        if (nativeDir) (*env)->ReleaseStringUTFChars(env, jNativeDir, nativeDir);
        if (logPath) (*env)->ReleaseStringUTFChars(env, jLogPath, logPath);
        return -10;
    }

    llama_server_fn fn = (llama_server_fn)dlsym(handle, "_Z12llama_serveriPPc");
    if (!fn) {
        log_str("[HubJNI] dlsym(_Z12llama_serveriPPc) failed: ");
        log_str(dlerror());
        log_str("\n");
        for (jsize i = 0; i < argc; i++) free(argv[i]);
        free(argv);
        if (nativeDir) (*env)->ReleaseStringUTFChars(env, jNativeDir, nativeDir);
        if (logPath) (*env)->ReleaseStringUTFChars(env, jLogPath, logPath);
        return -11;
    }

    log_str("[HubJNI] Starting llama_server inside Android classloader-namespace (NPU/GPU ready)...\n");
    int rc = fn((int)argc, argv);

    char msg[96];
    snprintf(msg, sizeof(msg), "[HubJNI] llama_server exited with code %d\n", rc);
    log_str(msg);

    for (jsize i = 0; i < argc; i++) free(argv[i]);
    free(argv);
    if (nativeDir) (*env)->ReleaseStringUTFChars(env, jNativeDir, nativeDir);
    if (logPath) (*env)->ReleaseStringUTFChars(env, jLogPath, logPath);
    return rc;
}
