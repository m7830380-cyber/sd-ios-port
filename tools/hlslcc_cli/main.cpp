// Batch DXBC -> Metal translator with reflection JSON output.
// Usage: hlslcc_cli <listfile>   each line: <in.dxbc>\t<out.metal>\t<out.json>\t<flags>
#include "hlslcc.h"
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <iostream>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <crtdbg.h>
#include <eh.h>
#include <DbgHelp.h>

static void se_trans_func(unsigned int u, EXCEPTION_POINTERS* pExp) {
    char buf[1024];
    DWORD64 addr = (DWORD64)pExp->ExceptionRecord->ExceptionAddress;
    uintptr_t base = (uintptr_t)GetModuleHandle(NULL);
    SymInitialize(GetCurrentProcess(), NULL, TRUE);
    char symBuf[sizeof(SYMBOL_INFO) + 256]{};
    PSYMBOL_INFO pSym = (PSYMBOL_INFO)symBuf;
    pSym->SizeOfStruct = sizeof(SYMBOL_INFO);
    pSym->MaxNameLen = 255;
    DWORD64 disp = 0;
    IMAGEHLP_LINE64 line{};
    line.SizeOfStruct = sizeof(IMAGEHLP_LINE64);
    DWORD lineDisp = 0;
    std::string name = "unknown";
    std::string file = "unknown";
    int lineNo = 0;
    if (SymFromAddr(GetCurrentProcess(), addr, &disp, pSym)) {
        name = pSym->Name;
    }
    if (SymGetLineFromAddr64(GetCurrentProcess(), addr, &lineDisp, &line)) {
        file = line.FileName;
        lineNo = line.LineNumber;
    }
    sprintf_s(buf, "SEH 0x%08X (RVA 0x%llX) in %s (%s:%d)", u, addr - base, name.c_str(), file.c_str(), lineNo);
    throw std::runtime_error(buf);
}
#endif

static std::string esc(const std::string& s){ std::string o; for(char c: s){ if(c==34||c==92){o+=(char)92;o+=c;} else if(c==10){o+=(char)92;o+=(char)110;} else if((unsigned char)c<32){} else o+=c;} return o; }

struct Refl : HLSLccReflection {
  std::ostringstream j; bool first=true;
  void item(const std::string& s){ j << (first?"":",\n") << s; first=false; }
  void OnDiagnostics(const std::string &e, int line, bool isError) override { std::ostringstream o; o<<"{\"k\":\"diag\",\"msg\":\""<<esc(e)<<"\",\"err\":"<<isError<<"}"; item(o.str()); }
  void OnInputBinding(const std::string &name, int bindIndex) override { std::ostringstream o; o<<"{\"k\":\"input\",\"name\":\""<<esc(name)<<"\",\"index\":"<<bindIndex<<"}"; item(o.str()); }
  bool OnConstantBuffer(const std::string &name, size_t size, size_t count) override { std::ostringstream o; o<<"{\"k\":\"cb\",\"name\":\""<<esc(name)<<"\",\"size\":"<<size<<",\"count\":"<<count<<"}"; item(o.str()); return true; }
  bool OnConstant(const std::string &name, int bindIndex, SHADER_VARIABLE_TYPE t, int rows, int cols, bool isMatrix, int arraySize, bool isUsed) override { std::ostringstream o; o<<"{\"k\":\"const\",\"name\":\""<<esc(name)<<"\",\"index\":"<<bindIndex<<",\"type\":"<<(int)t<<",\"rows\":"<<rows<<",\"cols\":"<<cols<<",\"isMatrix\":"<<isMatrix<<",\"arraySize\":"<<arraySize<<",\"used\":"<<isUsed<<"}"; item(o.str()); return true; }
  void OnConstantBufferBinding(const std::string &name, int bindIndex) override { std::ostringstream o; o<<"{\"k\":\"cbbind\",\"name\":\""<<esc(name)<<"\",\"index\":"<<bindIndex<<"}"; item(o.str()); }
  void OnTextureBinding(const std::string &name, int bindIndex, int samplerIndex, bool ms, HLSLCC_TEX_DIMENSION dim, bool isUAV) override { std::ostringstream o; o<<"{\"k\":\"tex\",\"name\":\""<<esc(name)<<"\",\"index\":"<<bindIndex<<",\"sampler\":"<<samplerIndex<<",\"ms\":"<<ms<<",\"dim\":"<<(int)dim<<",\"uav\":"<<isUAV<<"}"; item(o.str()); }
  void OnBufferBinding(const std::string &name, int bindIndex, bool isUAV) override { std::ostringstream o; o<<"{\"k\":\"buf\",\"name\":\""<<esc(name)<<"\",\"index\":"<<bindIndex<<",\"uav\":"<<isUAV<<"}"; item(o.str()); }
  void OnThreadGroupSize(unsigned x, unsigned y, unsigned z) override { std::ostringstream o; o<<"{\"k\":\"tgs\",\"x\":"<<x<<",\"y\":"<<y<<",\"z\":"<<z<<"}"; item(o.str()); }
  void OnVertexProgramOutput(const std::string& name, const std::string& sem, int semIdx) override { std::ostringstream o; o<<"{\"k\":\"vsout\",\"name\":\""<<esc(name)<<"\",\"sem\":\""<<esc(sem)<<"\",\"idx\":"<<semIdx<<"}"; item(o.str()); }
  void OnBuiltinOutput(SPECIAL_NAME n) override { std::ostringstream o; o<<"{\"k\":\"builtin\",\"name\":"<<(int)n<<"}"; item(o.str()); }
  void OnFragmentOutputDeclaration(int n, int idx) override { std::ostringstream o; o<<"{\"k\":\"fsout\",\"comps\":"<<n<<",\"idx\":"<<idx<<"}"; item(o.str()); }
};

static bool readFile(const std::string& p, std::vector<char>& d){ std::ifstream f(p, std::ios::binary); if(!f) return false; d.assign(std::istreambuf_iterator<char>(f), {}); return true; }

int main(int argc, char** argv){
#ifdef _WIN32
  _set_se_translator(se_trans_func);
  _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
  _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
  _CrtSetReportMode(_CRT_ERROR, _CRTDBG_MODE_FILE);
  _CrtSetReportFile(_CRT_ERROR, _CRTDBG_FILE_STDERR);
  _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif

  if(argc<2){ fprintf(stderr,"usage\n"); return 1; }
  std::ifstream lf(argv[1]); std::string line; int fails=0, n=0;
  while(std::getline(lf,line)){
    while(!line.empty() && (line.back()=='\r' || line.back()=='\n')) line.pop_back();
    if(line.empty()) continue;
    std::istringstream ss(line); std::string in,outm,outj,fl;
    std::getline(ss,in,'\t'); std::getline(ss,outm,'\t'); std::getline(ss,outj,'\t'); std::getline(ss,fl,'\t');
    unsigned flags = fl.empty()?0:(unsigned)std::stoul(fl,nullptr,0);
    std::vector<char> d;
    if(!readFile(in,d)){ fprintf(stderr,"cannot read %s\n",in.c_str()); fails++; continue; }
    Refl r;
    GLSLShader res{};
    GLSLCrossDependencyData deps;
    HLSLccSamplerPrecisionInfo sp;
    GlExtensions ext{};
    int ok = 0;
    try {
        ok = TranslateHLSLFromMem(d.data(), flags, LANG_METAL, &ext, nullptr, sp, r, &res);
        if(!ok) fprintf(stderr, "TranslateHLSLFromMem returned 0 on %s\n", in.c_str());
    } catch(const std::exception& e) {
        fprintf(stderr, "exception on %s: %s\n", in.c_str(), e.what());
        ok = 0;
    } catch(...) {
        fprintf(stderr, "unknown/SEH on %s\n", in.c_str());
        ok = 0;
    }
    n++;
    std::ofstream oj(outj, std::ios::binary);
    oj << "{\"ok\":"<<ok<<",\"shaderType\":"<<res.shaderType<<",\"refl\":[\n"<<r.j.str()<<"]}";
    if(ok){ std::ofstream om(outm, std::ios::binary); om<<res.sourceCode; } else fails++;
  }
  fprintf(stderr,"translated %d, failed %d\n", n, fails);
  return 0;
}
